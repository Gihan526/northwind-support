using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Abstractions;
using SupportDesk.Application.Contracts.Common;
using SupportDesk.Application.Contracts.Tickets;
using SupportDesk.Application.Services;
using SupportDesk.Domain.Aggregates.Agents;
using SupportDesk.Domain.Aggregates.Categories;
using SupportDesk.Domain.Aggregates.Customers;
using SupportDesk.Domain.Aggregates.Tickets;
using SupportDesk.Infrastructure.Data;

namespace SupportDesk.Infrastructure.Queries;

/// <summary>
/// Ticket reads. Projected in the database and never tracked. Aggregates hold only each
/// other's ids, so the customer, category and agent labels are joined in here, on the read side.
/// </summary>
public sealed class TicketQueries(SupportDbContext db, IClock clock, SlaSettings? settings = null) : ITicketQueries
{
    private readonly SlaSettings _sla = settings ?? new SlaSettings();

    /// <remarks>
    /// Filters compose on the database query before counting, sorting and paging.
    /// </remarks>
    public async Task<PagedResult<TicketListItemDto>> GetPagedAsync(TicketQuery query, CancellationToken ct)
    {
        var now = clock.UtcNow; // Same time for filtering and displayed status.
        var tickets = ApplyFilters(TicketsWithLabels(), query);
        if (query.SlaStatus is { } status) tickets = ApplySlaFilter(tickets, status, now, _sla.AtRiskThresholdPercent);
        var totalCount = await tickets.CountAsync(ct);

        var items = await ApplySort(tickets, query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new TicketListItemDto
            {
                Id = x.Ticket.Id,
                Reference = x.Ticket.Reference,
                Title = x.Ticket.Title,
                Status = x.Ticket.Status,
                Priority = x.Ticket.Priority,
                Customer = new CustomerSummaryDto(x.Customer.Id, x.Customer.Name, x.Customer.Tier),
                Category = new CategorySummaryDto(x.Category.Id, x.Category.Name),
                AssignedAgent = x.Agent == null ? null : new AgentSummaryDto(x.Agent.Id, x.Agent.FullName),
                CreatedAtUtc = x.Ticket.CreatedAtUtc,
                UpdatedAtUtc = x.Ticket.UpdatedAtUtc,
                DueAtUtc = x.Ticket.DueAtUtc,
                ResolvedAtUtc = x.Ticket.ResolvedAtUtc,
                SlaStatus = SlaEvaluator.Evaluate(x.Ticket.DueAtUtc, x.Ticket.ResolvedAtUtc, now,
                    x.Ticket.SlaWindowMilliseconds, _sla.AtRiskThresholdPercent)
            })
            .ToListAsync(ct);

        return new PagedResult<TicketListItemDto>(items, query.Page, query.PageSize, totalCount);
    }

    public Task<TicketDetailDto?> GetDetailAsync(int id, CancellationToken ct)
    {
        var now = clock.UtcNow;

        return TicketsWithLabels()
            .Where(x => x.Ticket.Id == id)
            .Select(x => new TicketDetailDto
            {
                Id = x.Ticket.Id,
                Reference = x.Ticket.Reference,
                Title = x.Ticket.Title,
                Description = x.Ticket.Description,
                Status = x.Ticket.Status,
                Priority = x.Ticket.Priority,
                Customer = new CustomerContactDto(
                    x.Customer.Id, x.Customer.Name, x.Customer.Email, x.Customer.Phone, x.Customer.Tier),
                Category = new CategoryDto(x.Category.Id, x.Category.Name, x.Category.RequiresSpecialist),
                AssignedAgent = x.Agent == null ? null : new AgentSummaryDto(x.Agent.Id, x.Agent.FullName),
                CreatedAtUtc = x.Ticket.CreatedAtUtc,
                UpdatedAtUtc = x.Ticket.UpdatedAtUtc,
                DueAtUtc = x.Ticket.DueAtUtc,
                ResolvedAtUtc = x.Ticket.ResolvedAtUtc,
                SlaStatus = SlaEvaluator.Evaluate(x.Ticket.DueAtUtc, x.Ticket.ResolvedAtUtc, now,
                    x.Ticket.SlaWindowMilliseconds, _sla.AtRiskThresholdPercent)
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<TicketListItemDto>> GetForCustomerAsync(int customerId, CancellationToken ct)
    {
        var now = clock.UtcNow;

        return await TicketsWithLabels()
            .Where(x => x.Ticket.CustomerId == customerId)
            .OrderByDescending(x => x.Ticket.CreatedAtUtc)
            .ThenByDescending(x => x.Ticket.Id)
            .Select(x => new TicketListItemDto
            {
                Id = x.Ticket.Id,
                Reference = x.Ticket.Reference,
                Title = x.Ticket.Title,
                Status = x.Ticket.Status,
                Priority = x.Ticket.Priority,
                Customer = new CustomerSummaryDto(x.Customer.Id, x.Customer.Name, x.Customer.Tier),
                Category = new CategorySummaryDto(x.Category.Id, x.Category.Name),
                AssignedAgent = x.Agent == null ? null : new AgentSummaryDto(x.Agent.Id, x.Agent.FullName),
                CreatedAtUtc = x.Ticket.CreatedAtUtc,
                UpdatedAtUtc = x.Ticket.UpdatedAtUtc,
                DueAtUtc = x.Ticket.DueAtUtc,
                ResolvedAtUtc = x.Ticket.ResolvedAtUtc,
                SlaStatus = SlaEvaluator.Evaluate(x.Ticket.DueAtUtc, x.Ticket.ResolvedAtUtc, now,
                    x.Ticket.SlaWindowMilliseconds, _sla.AtRiskThresholdPercent)
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AssignmentCandidate>> GetAssignmentCandidatesAsync(int categoryId, int? excludedTicketId, CancellationToken ct) =>
        await db.Set<Agent>().AsNoTracking().Select(agent => new AssignmentCandidate(
            agent.Id, agent.FullName, agent.IsActive,
            db.Set<Ticket>().Count(ticket => ticket.AssignedAgentId == agent.Id
                && ticket.Status != TicketStatus.Resolved && ticket.Status != TicketStatus.Closed
                && (excludedTicketId == null || ticket.Id != excludedTicketId)),
            agent.MaxOpenTickets, agent.Specializations.Any(s => s.CategoryId == categoryId))).ToListAsync(ct);

    public async Task<IReadOnlyList<TicketEscalationDto>> GetEscalationsAsync(int ticketId, CancellationToken ct) =>
        await (from row in db.Set<TicketEscalation>().AsNoTracking()
               from before in db.Set<Agent>().Where(agent => agent.Id == row.FromAgentId).DefaultIfEmpty()
               from after in db.Set<Agent>().Where(agent => agent.Id == row.ToAgentId).DefaultIfEmpty()
               where row.TicketId == ticketId
               orderby row.EscalatedAtUtc descending, row.Id descending
               select new TicketEscalationDto
               {
                   Id = row.Id,
                   TicketId = row.TicketId,
                   FromPriority = row.FromPriority,
                   ToPriority = row.ToPriority,
                   FromAgent = before == null ? null : new AgentSummaryDto(before.Id, before.FullName),
                   ToAgent = after == null ? null : new AgentSummaryDto(after.Id, after.FullName),
                   FromDueAtUtc = row.FromDueAtUtc,
                   ToDueAtUtc = row.ToDueAtUtc,
                   Reason = row.Reason,
                   EscalatedBy = row.EscalatedBy,
                   EscalatedAtUtc = row.EscalatedAtUtc
               }).ToListAsync(ct);

    // Same cases as SlaEvaluator, expressed in SQL before counting and paging.
    private static IQueryable<TicketWithLabels> ApplySlaFilter(IQueryable<TicketWithLabels> source,
        SlaStatus status, DateTime now, double percent) => status switch
        {
            SlaStatus.NotApplicable => source.Where(x => x.Ticket.DueAtUtc == null),
            SlaStatus.Met => source.Where(x => x.Ticket.DueAtUtc != null && x.Ticket.ResolvedAtUtc != null
                && x.Ticket.ResolvedAtUtc <= x.Ticket.DueAtUtc),
            SlaStatus.Breached => source.Where(x => x.Ticket.DueAtUtc != null
                && ((x.Ticket.ResolvedAtUtc != null && x.Ticket.ResolvedAtUtc > x.Ticket.DueAtUtc)
                    || (x.Ticket.ResolvedAtUtc == null && x.Ticket.DueAtUtc < now))),
            SlaStatus.AtRisk => source.Where(x => x.Ticket.DueAtUtc != null && x.Ticket.ResolvedAtUtc == null
                && x.Ticket.DueAtUtc >= now && x.Ticket.SlaWindowMilliseconds > 0
                && now >= x.Ticket.DueAtUtc.Value.AddMilliseconds(-Math.Floor(x.Ticket.SlaWindowMilliseconds * percent / 100))),
            SlaStatus.WithinSla => source.Where(x => x.Ticket.DueAtUtc != null && x.Ticket.ResolvedAtUtc == null
                && x.Ticket.DueAtUtc >= now && (x.Ticket.SlaWindowMilliseconds <= 0
                    || now < x.Ticket.DueAtUtc.Value.AddMilliseconds(-Math.Floor(x.Ticket.SlaWindowMilliseconds * percent / 100)))),
            _ => source.Where(x => false)
        };

    /// <summary>
    /// Every ticket with the rows that label it. Filter and sort this, then project.
    /// </summary>
    private IQueryable<TicketWithLabels> TicketsWithLabels() =>
        from ticket in db.Set<Ticket>().AsNoTracking()
        join customer in db.Set<Customer>() on ticket.CustomerId equals customer.Id
        join category in db.Set<Category>() on ticket.CategoryId equals category.Id
        from agent in db.Set<Agent>().Where(a => a.Id == ticket.AssignedAgentId).DefaultIfEmpty()
        select new TicketWithLabels { Ticket = ticket, Customer = customer, Category = category, Agent = agent };

    private static IQueryable<TicketWithLabels> ApplyFilters(IQueryable<TicketWithLabels> source, TicketQuery query)
    {
        if (query.Status is { } status)
        {
            source = source.Where(x => x.Ticket.Status == status);
        }

        if (query.Priority is { } priority)
        {
            source = source.Where(x => x.Ticket.Priority == priority);
        }

        if (query.CategoryId is { } categoryId)
        {
            source = source.Where(x => x.Ticket.CategoryId == categoryId);
        }

        if (query.CustomerId is { } customerId)
        {
            source = source.Where(x => x.Ticket.CustomerId == customerId);
        }

        if (query.AssignedAgentId is { } agentId)
        {
            source = source.Where(x => x.Ticket.AssignedAgentId == agentId);
        }

        if (query.UnassignedOnly)
        {
            source = source.Where(x => x.Ticket.AssignedAgentId == null);
        }

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            source = source.Where(x => x.Ticket.Title.Contains(search)
                || x.Ticket.Reference.Contains(search)
                || x.Customer.Name.Contains(search));
        }

        return source;
    }

    private static IQueryable<TicketWithLabels> ApplySort(IQueryable<TicketWithLabels> source, TicketQuery query)
    {
        var descending = !string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);

        var sorted = query.SortBy?.ToLowerInvariant() switch
        {
            "updatedatutc" => descending
                ? source.OrderByDescending(x => x.Ticket.UpdatedAtUtc)
                : source.OrderBy(x => x.Ticket.UpdatedAtUtc),
            "dueatutc" => descending
                ? source.OrderByDescending(x => x.Ticket.DueAtUtc)
                : source.OrderBy(x => x.Ticket.DueAtUtc),
            "priority" => descending
                ? source.OrderByDescending(x => x.Ticket.Priority)
                : source.OrderBy(x => x.Ticket.Priority),
            "status" => descending
                ? source.OrderByDescending(x => x.Ticket.Status)
                : source.OrderBy(x => x.Ticket.Status),
            _ => descending
                ? source.OrderByDescending(x => x.Ticket.CreatedAtUtc)
                : source.OrderBy(x => x.Ticket.CreatedAtUtc)
        };

        // Keeps paging stable when the sort column has duplicates.
        return sorted.ThenByDescending(x => x.Ticket.Id);
    }

    private sealed class TicketWithLabels
    {
        public required Ticket Ticket { get; init; }

        public required Customer Customer { get; init; }

        public required Category Category { get; init; }

        public Agent? Agent { get; init; }
    }
}
