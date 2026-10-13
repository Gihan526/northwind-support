using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Infrastructure.Data;
using SupportDesk.Presentation.Extensions;

namespace SupportDesk.UnitTests.Presentation;

public sealed class HealthHttpTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.OK, "Healthy")]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    public async Task Health_IsAnonymousAndReportsDatabaseReachability(
        bool reachable, HttpStatusCode expectedStatus, string expectedBody)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration["Jwt:SigningKey"] = "Health_Test_Only_Signing_Key_At_Least_32_Bytes";
        builder.Services.AddAgentAuthentication(builder.Configuration);

        // Mode=ReadWrite fails if the file does not exist; no directory/file is created.
        var missingFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "missing.db");
        var connectionString = reachable ? "Data Source=:memory:" : $"Data Source={missingFile};Mode=ReadWrite";
        builder.Services.AddDbContext<SupportDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", timeout: TimeSpan.FromSeconds(5));

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health").AllowAnonymous();
        app.MapGet("/protected", () => "Private");
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/health");
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/protected")).StatusCode);
    }
}
