using SupportDesk.Application.Abstractions;
using SupportDesk.Application.Contracts.Tickets;
using SupportDesk.Domain.Aggregates.Categories;
using SupportDesk.Domain.Aggregates.Customers;
using SupportDesk.Domain.Aggregates.Tickets;

namespace SupportDesk.Application.Services;

// Create and escalate share these rules. Queries supply facts; this service makes decisions.
public sealed class TicketTriageService(ITicketQueries queries, SlaSettings settings)
{
    public Task<TriageDecision> ForCreateAsync(Customer customer, Category category, TicketPriority? requested, CancellationToken ct)
    {
        var priority = requested ?? TicketPriority.Medium;
        var reason = requested is null ? "Defaulted to Medium." : $"Used requested priority {priority}.";
        if (category.ForcesCriticalPriority)
        {
            priority = TicketPriority.Critical;
            reason = "The category is flagged to force Critical priority.";
        }
        return DecideAsync(customer, category, priority, reason, null, null, ct);
    }

    public Task<TriageDecision> ForEscalationAsync(Ticket ticket, Customer customer, Category category, CancellationToken ct) =>
        DecideAsync(customer, category, ticket.GetEscalatedPriority(), $"Escalated from {ticket.Priority} by one level.",
            ticket.AssignedAgentId, ticket.Id, ct);

    public TimeSpan CalculateWindow(TicketPriority priority, CustomerTier tier)
    {
        var hours = settings.BaseWindowHours[priority];
        if (tier == CustomerTier.Premium)
            hours = Math.Max(hours * settings.PremiumCustomerMultiplier, settings.MinimumWindowHours);
        return TimeSpan.FromHours(hours);
    }

    public static AssignmentCandidate? ChooseAgent(IReadOnlyList<AssignmentCandidate> candidates, bool specialist, int? currentId = null)
    {
        var eligible = candidates.Where(agent => agent.IsActive && agent.OpenTicketCount < agent.MaxOpenTickets
            && (!specialist || agent.HasSpecialization)).ToList();
        // Escalation keeps an eligible owner, even when another agent has fewer tickets.
        return eligible.FirstOrDefault(agent => agent.Id == currentId)
            ?? eligible.OrderBy(agent => agent.OpenTicketCount).ThenBy(agent => agent.Id).FirstOrDefault();
    }

    private async Task<TriageDecision> DecideAsync(Customer customer, Category category, TicketPriority priority,
        string priorityReason, int? currentId, int? excludedTicketId, CancellationToken ct)
    {
        var window = CalculateWindow(priority, customer.Tier);
        var candidates = await queries.GetAssignmentCandidatesAsync(category.Id, excludedTicketId, ct);
        var agent = ChooseAgent(candidates, category.RequiresSpecialist, currentId);
        var assignment = agent is null ? "Unassigned because no active, qualified agent has spare capacity."
            : agent.Id == currentId ? $"Kept {agent.FullName}: still eligible."
            : $"Assigned {agent.FullName}: fewest open tickets ({agent.OpenTicketCount}); ties use agent ID.";
        return new TriageDecision(priority, agent?.Id, window,
            new TicketTriageDto(priorityReason, $"{window.TotalHours:g} hour window for {priority} / {customer.Tier}, using the configured SLA rules.", assignment));
    }
}

public sealed record TriageDecision(TicketPriority Priority, int? AgentId, TimeSpan Window, TicketTriageDto Explanation);

// ASP.NET binds the existing Sla configuration section at startup.
public sealed class SlaSettings
{
    public Dictionary<TicketPriority, double> BaseWindowHours { get; set; } = new()
    {
        [TicketPriority.Low] = 72,
        [TicketPriority.Medium] = 24,
        [TicketPriority.High] = 8,
        [TicketPriority.Critical] = 4
    };
    public double PremiumCustomerMultiplier { get; set; } = 0.5;
    public double MinimumWindowHours { get; set; } = 1;
    public double AtRiskThresholdPercent { get; set; } = 25;

    public void Validate()
    {
        if (BaseWindowHours.Values.Any(hours => !double.IsFinite(hours) || hours <= 0)
            || !double.IsFinite(PremiumCustomerMultiplier) || PremiumCustomerMultiplier <= 0
            || !double.IsFinite(MinimumWindowHours) || MinimumWindowHours <= 0
            || !double.IsFinite(AtRiskThresholdPercent) || AtRiskThresholdPercent < 0 || AtRiskThresholdPercent > 100)
            throw new InvalidOperationException("SLA windows/multiplier must be positive; risk percentage must be 0–100.");
    }
}
