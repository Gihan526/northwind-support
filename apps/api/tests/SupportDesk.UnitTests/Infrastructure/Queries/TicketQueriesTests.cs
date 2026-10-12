using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SupportDesk.Application.Contracts.Tickets;
using SupportDesk.Domain.Aggregates.Agents;
using SupportDesk.Domain.Aggregates.Categories;
using SupportDesk.Domain.Aggregates.Customers;
using SupportDesk.Domain.Aggregates.Tickets;
using SupportDesk.Infrastructure.Data;
using SupportDesk.Infrastructure.Queries;
using SupportDesk.UnitTests.TestDoubles;

namespace SupportDesk.UnitTests.Infrastructure.Queries;

// SQLite executes real relational queries without requiring a developer's SQL Server.
public sealed class TicketQueriesTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SqlCapture _sql = new();
    private SupportDbContext _db = null!;
    private TicketQueries _queries = null!;
    private Customer _contoso = null!;
    private Customer _fabrikam = null!;
    private Category _general = null!;
    private Category _billing = null!;
    private Agent _alex = null!;
    private Agent _sara = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SupportDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_sql)
            .Options;
        _db = new SupportDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        _queries = new TicketQueries(_db, new FixedClock());

        _contoso = new Customer("Contoso", "help@contoso.test", null, CustomerTier.Premium, FixedClock.DefaultNow);
        _fabrikam = new Customer("Fabrikam", "help@fabrikam.test", null, CustomerTier.Standard, FixedClock.DefaultNow);
        _general = new Category("General", false, false);
        _billing = new Category("Billing", true, false);
        _alex = new Agent("Alex Turner", "alex@example.test", 10, FixedClock.DefaultNow);
        _sara = new Agent("Sara Lindqvist", "sara@example.test", 6, FixedClock.DefaultNow);
        _db.AddRange(_contoso, _fabrikam, _general, _billing, _alex, _sara);
        await _db.SaveChangesAsync();

        AddTicket("TCK-0042", "Invoice missing", TicketPriority.High, TicketStatus.InProgress, _contoso, _billing, _sara);
        AddTicket("TCK-0043", "Printer broken", TicketPriority.High, TicketStatus.Open, _fabrikam, _general, _alex);
        AddTicket("TCK-0044", "Update account", TicketPriority.Low, TicketStatus.New, _contoso, _general);
        AddTicket("TCK-0045", "Refund request", TicketPriority.Medium, TicketStatus.Resolved, _fabrikam, _billing, _sara);
        AddTicket("TCK-0046", "Payment failed", TicketPriority.High, TicketStatus.Open, _contoso, _billing);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _sql.Commands.Clear();
    }

    [Fact]
    public async Task Priority_IsAnExactMatch()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery { Priority = TicketPriority.High }, default);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.TotalPages);
        Assert.All(result.Items, ticket => Assert.Equal(TicketPriority.High, ticket.Priority));
    }

    [Fact]
    public async Task Status_IsAnExactMatch()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery { Status = TicketStatus.InProgress }, default);

        Assert.Equal("TCK-0042", Assert.Single(result.Items).Reference);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task Category_IsAnExactMatch()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery { CategoryId = _billing.Id }, default);

        Assert.Equal(3, result.TotalCount);
        Assert.All(result.Items, ticket => Assert.Equal(_billing.Id, ticket.Category.Id));
    }

    [Fact]
    public async Task Customer_IsAnExactMatch()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery { CustomerId = _contoso.Id }, default);

        Assert.Equal(3, result.TotalCount);
        Assert.All(result.Items, ticket => Assert.Equal(_contoso.Id, ticket.Customer.Id));
    }

    [Fact]
    public async Task AssignedAgent_IsAnExactMatch()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery { AssignedAgentId = _sara.Id }, default);

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, ticket => Assert.Equal(_sara.Id, ticket.AssignedAgent!.Id));
    }

    [Fact]
    public async Task SuppliedFilters_CombineWithAnd()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery
        {
            Status = TicketStatus.InProgress,
            Priority = TicketPriority.High,
            CategoryId = _billing.Id,
            CustomerId = _contoso.Id,
            AssignedAgentId = _sara.Id,
            Search = "Invoice"
        }, default);

        Assert.Equal("TCK-0042", Assert.Single(result.Items).Reference);
        Assert.Equal(1, result.TotalCount);
    }

    [Theory]
    [InlineData("  Contoso  ", 3)]
    [InlineData("Invoice", 1)]
    [InlineData("TCK-0042", 1)]
    public async Task Search_MatchesCustomerTitleOrReference_AndTrims(string search, int expectedCount)
    {
        var result = await _queries.GetPagedAsync(new TicketQuery { Search = search }, default);

        Assert.Equal(expectedCount, result.TotalCount);
        Assert.Equal(expectedCount, result.Items.Count);
        if (expectedCount == 1) Assert.Equal("TCK-0042", result.Items[0].Reference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSearch_IsIgnored(string? search)
    {
        var result = await _queries.GetPagedAsync(new TicketQuery { Search = search }, default);

        Assert.Equal(5, result.TotalCount);
    }

    [Fact]
    public async Task UnassignedOnly_CombinesWithOtherFilters()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery
        {
            UnassignedOnly = true,
            Priority = TicketPriority.High
        }, default);

        var ticket = Assert.Single(result.Items);
        Assert.Equal("TCK-0046", ticket.Reference);
        Assert.Null(ticket.AssignedAgent);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task AssignedAgentAndUnassignedOnly_AreContradictory()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery
        {
            AssignedAgentId = _sara.Id,
            UnassignedOnly = true
        }, default);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.TotalPages);
    }

    [Fact]
    public async Task FilteringPrecedesCountingSortingAndPaging_InSql()
    {
        var query = new TicketQuery
        {
            Priority = TicketPriority.High,
            Page = 2,
            PageSize = 2,
            SortBy = "createdAtUtc",
            SortDirection = "asc"
        };
        var result = await _queries.GetPagedAsync(query, default);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal("TCK-0042", Assert.Single(result.Items).Reference);
        Assert.Empty(_db.ChangeTracker.Entries());

        // Both count and page must execute against the filtered database set.
        Assert.Equal(2, _sql.Commands.Count);
        Assert.Contains("COUNT(*)", _sql.Commands[0]);
        Assert.All(_sql.Commands, command =>
        {
            Assert.Contains("WHERE", command);
            Assert.Contains("\"Priority\" =", command);
        });
        Assert.Contains("ORDER BY", _sql.Commands[1]);
        Assert.Contains("LIMIT", _sql.Commands[1]);
        Assert.Contains("OFFSET", _sql.Commands[1]);
    }

    [Fact]
    public async Task PageBeyondFilteredResults_IsEmptyButRetainsCount()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery
        {
            Priority = TicketPriority.High,
            Page = 4,
            PageSize = 1
        }, default);

        Assert.Empty(result.Items);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.TotalPages);
    }

    [Fact]
    public async Task UnknownCustomer_ReturnsAnEmptyPage()
    {
        var result = await _queries.GetPagedAsync(new TicketQuery { CustomerId = 999 }, default);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.TotalPages);
    }

    private void AddTicket(string reference, string title, TicketPriority priority, TicketStatus status,
        Customer customer, Category category, Agent? agent = null)
    {
        var ticket = Ticket.Raise(reference, title, "A realistic description", customer.Id, category.Id,
            priority, FixedClock.DefaultNow);
        if (agent is not null) ticket.AssignTo(agent.Id, FixedClock.DefaultNow);
        if (status != TicketStatus.New) ticket.ChangeStatus(status, FixedClock.DefaultNow);
        _db.Add(ticket);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
