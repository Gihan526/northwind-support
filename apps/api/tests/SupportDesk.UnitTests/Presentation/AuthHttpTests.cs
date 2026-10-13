using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SupportDesk.Application;
using SupportDesk.Application.Abstractions;
using SupportDesk.Application.Contracts.Auth;
using SupportDesk.Application.Contracts.Common;
using SupportDesk.Application.Contracts.Tickets;
using SupportDesk.Application.Services;
using SupportDesk.Domain.Aggregates.Agents;
using SupportDesk.Domain.Aggregates.Tickets;
using SupportDesk.Domain.Repositories;
using SupportDesk.Infrastructure.Data;
using SupportDesk.Infrastructure.Queries;
using SupportDesk.Infrastructure.Repositories;
using SupportDesk.Infrastructure.Services;
using SupportDesk.Presentation.Controllers;
using SupportDesk.Presentation.Extensions;
using SupportDesk.Presentation.Filters;
using SupportDesk.Presentation.Middleware;
using SupportDesk.UnitTests.TestDoubles;

namespace SupportDesk.UnitTests.Presentation;

// Real HTTP authentication/authorization, hashing and SQLite data; no Docker required.
public sealed class AuthHttpTests : IAsyncLifetime
{
    private const string Key = "Test_Only_Signing_Key_With_At_Least_32_Bytes";
    private const string Email = "alex@example.test";
    private const string Password = "TestOnly_Login_123!";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FixedClock _clock = new(DateTime.UtcNow);
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _agentId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration["Jwt:SigningKey"] = Key;
        builder.Configuration["DevelopmentSeed:AgentPassword"] = "TestOnly_Seed_123!";
        builder.Services.AddControllers(options => options.Filters.Add<ValidationFilter>())
            .AddApplicationPart(typeof(AuthController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton(new SlaSettings());
        builder.Services.AddApplication();
        builder.Services.AddDbContext<SupportDbContext>(options => options.UseSqlite(_connection));
        builder.Services.AddSingleton<IClock>(_clock);
        builder.Services.AddSingleton<IPasswordService, PasswordService>();
        builder.Services.AddScoped<IAgentRepository, AgentRepository>();
        builder.Services.AddScoped<ITicketQueries, TicketQueries>();
        builder.Services.AddScoped<ITicketRepository, TicketRepository>();
        builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
        builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
        builder.Services.AddScoped<IUnitOfWork>(services => services.GetRequiredService<SupportDbContext>());
        builder.Services.AddScoped<SupportDbSeeder>();
        builder.Services.AddAgentAuthentication(builder.Configuration);
        _app = builder.Build();
        _app.UseMiddleware<ExceptionHandlingMiddleware>();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        await db.Database.EnsureCreatedAsync();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var agent = new Agent("Alex Turner", Email, 10, _clock.UtcNow);
        agent.SetPasswordHash(passwords.Hash(Email, Password));
        var inactive = new Agent("Inactive Agent", "inactive@example.test", 10, _clock.UtcNow);
        inactive.SetPasswordHash(passwords.Hash(inactive.Email, Password));
        inactive.Deactivate();
        db.AddRange(agent, inactive);
        await db.SaveChangesAsync();
        _agentId = agent.Id;
    }

    [Theory]
    [InlineData("/api/tickets")]
    [InlineData("/api/tickets/1")]
    [InlineData("/api/customers")]
    [InlineData("/api/agents")]
    [InlineData("/api/categories")]
    [InlineData("/api/auth/me")]
    public async Task MissingToken_Is401AcrossExistingEndpoints(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [Fact]
    public async Task WrongEmailWrongPasswordAndInactive_AreIdentical401WithNoToken()
    {
        var unknown = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("unknown@example.test", Password));
        var wrong = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Email, "Wrong_Password_123!"));
        var inactive = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("inactive@example.test", Password));
        var expected = await unknown.Content.ReadAsStringAsync();
        Assert.Contains("Email or password is incorrect.", expected);
        Assert.DoesNotContain("accessToken", expected);
        foreach (var response in new[] { unknown, wrong, inactive })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Login_IssuesOneHourTokenAndAllowsTicketsAndMe()
    {
        var login = await LoginAsync(" ALEX@EXAMPLE.TEST ");

        Assert.Equal("Alex Turner", login.Agent.FullName);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);
        Assert.Equal(jwt.ValidTo, login.ExpiresAtUtc);
        Assert.InRange(login.ExpiresAtUtc, _clock.UtcNow.AddHours(1).AddSeconds(-1), _clock.UtcNow.AddHours(1));
        Assert.Equal(_agentId.ToString(), jwt.Subject);
        Assert.Contains(jwt.Claims, claim => claim.Type == "name" && claim.Value == "Alex Turner");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var tickets = await _client.GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.OK, tickets.StatusCode);
        Assert.NotNull(await tickets.Content.ReadFromJsonAsync<PagedResult<TicketListItemDto>>());
        var me = await _client.GetFromJsonAsync<SignedInAgent>("/api/auth/me");
        Assert.Equal(login.Agent, me);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-key")]
    [InlineData("malformed")]
    public async Task InvalidToken_IsRejected(string kind)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: kind == "wrong-issuer" ? "OtherIssuer" : "NorthwindSupportDesk",
            audience: kind == "wrong-audience" ? "OtherAudience" : "NorthwindSupportDeskWeb",
            claims: [new Claim("sub", _agentId.ToString())],
            notBefore: now.AddHours(-2), expires: kind == "expired" ? now.AddHours(-1) : now.AddHours(1),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                kind == "wrong-key" ? "Different_Test_Key_With_At_Least_32_Bytes" : Key)), SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            kind == "malformed" ? "not-a-jwt" : new JwtSecurityTokenHandler().WriteToken(token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/tickets")).StatusCode);
    }

    [Fact]
    public async Task DeactivatedAgent_CannotKeepUsingAnIssuedToken()
    {
        var login = await LoginAsync();
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        (await db.Set<Agent>().FindAsync(_agentId))!.Deactivate();
        await db.SaveChangesAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/tickets")).StatusCode);
    }

    [Fact]
    public void PasswordHash_IsSaltedAndVerifiesCorrectly()
    {
        var passwords = new PasswordService();
        var first = passwords.Hash(Email, Password);
        var second = passwords.Hash(Email, Password);
        Assert.NotEqual(Password, first);
        Assert.NotEqual(first, second);
        Assert.True(passwords.Verify(Email, first, Password));
        Assert.False(passwords.Verify(Email, first, "Wrong password"));
    }

    [Fact]
    public async Task DevelopmentSeed_AddsMissingHashesWithoutResettingExistingPasswords()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var seeder = scope.ServiceProvider.GetRequiredService<SupportDbSeeder>();
        await seeder.SeedAsync();
        var hashes = await db.Set<Agent>().ToDictionaryAsync(agent => agent.Id, agent => agent.PasswordHash);
        Assert.All(hashes.Values, hash => Assert.NotNull(hash));
        await seeder.SeedAsync();
        Assert.Equal(hashes, await db.Set<Agent>().ToDictionaryAsync(agent => agent.Id, agent => agent.PasswordHash));
        var extra = new Agent("Extra Agent", "extra@example.test", 10, _clock.UtcNow);
        db.Add(extra);
        await db.SaveChangesAsync();
        await seeder.SeedAsync();
        Assert.True(passwords.Verify(extra.Email, extra.PasswordHash!, "TestOnly_Seed_123!"));
        var original = await db.Set<Agent>().FindAsync(_agentId);
        Assert.True(passwords.Verify(original!.Email, original.PasswordHash!, Password));
    }

    [Fact]
    public async Task InvalidLoginInput_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest("bad-email", ""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static readonly JsonSerializerOptions TicketJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Escalation_RecordsTokenActorAndIgnoresForgedBodyActor()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SupportDbSeeder>().SeedAsync();
        var login = await LoginAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var result = await _client.PostAsJsonAsync("/api/tickets/14/escalate",
            new { reason = "  Customer cannot work  ", escalatedBy = "Forged actor" });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var escalation = (await result.Content.ReadFromJsonAsync<EscalateTicketResponse>(TicketJson))!;
        Assert.Equal($"Alex Turner (agent #{_agentId})", escalation.Escalation.EscalatedBy);
        Assert.Equal("Customer cannot work", escalation.Escalation.Reason);
        Assert.Equal(TicketPriority.Low, escalation.Escalation.FromPriority);
        Assert.Equal(TicketPriority.Medium, escalation.Ticket.Priority);
        Assert.Equal(_clock.UtcNow.AddHours(24), escalation.Ticket.DueAtUtc);
        var history = await _client.GetFromJsonAsync<List<TicketEscalationDto>>("/api/tickets/14/escalations", TicketJson);
        Assert.Equal(escalation.Escalation.Id, Assert.Single(history!).Id);
    }

    [Theory]
    [InlineData("/api/tickets/14/escalate", 400, "abc")]
    [InlineData("/api/tickets/999999/escalate", 404, "Valid reason")]
    [InlineData("/api/tickets/6/escalate", 409, "Valid reason")]
    [InlineData("/api/tickets/25/escalate", 409, "Valid reason")]
    public async Task Escalation_HttpRejections(string path, int status, string reason)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SupportDbSeeder>().SeedAsync();
        var login = await LoginAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        Assert.Equal(status, (int)(await _client.PostAsJsonAsync(path, new { reason })).StatusCode);
    }

    [Fact]
    public async Task NewTicket_NoEligibleAgent_StillReturns201WithTriage()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        await scope.ServiceProvider.GetRequiredService<SupportDbSeeder>().SeedAsync();
        // Only the signed-in test agent remains active; fill their exact capacity.
        foreach (var agent in await db.Set<Agent>().Where(agent => agent.Id != _agentId).ToListAsync()) agent.Deactivate();
        for (var i = 0; i < 10; i++)
        {
            var ticket = Ticket.Raise($"CAP-{i}", "Capacity test", "A ticket occupying an agent slot", 1, 1, TicketPriority.Low, _clock.UtcNow);
            ticket.AssignTo(_agentId, _clock.UtcNow);
            db.Add(ticket);
        }
        await db.SaveChangesAsync();
        var login = await LoginAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var response = await _client.PostAsJsonAsync("/api/tickets", new CreateTicketRequest("New customer issue", "Customer needs help with the portal", 1, 1, TicketPriority.High), TicketJson);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<TicketDetailDto>(TicketJson))!;
        Assert.Null(result.AssignedAgent);
        Assert.Equal(TicketPriority.High, result.Priority);
        Assert.Equal(_clock.UtcNow.AddHours(4), result.DueAtUtc);
        Assert.Contains("Unassigned because", result.Triage!.AssignmentReason);
    }

    private async Task<LoginResponse> LoginAsync(string email = Email)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
