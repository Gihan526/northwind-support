using SupportDesk.Domain.Aggregates.Tickets;

namespace SupportDesk.Application.Contracts.Tickets;

/// <summary>
/// A single ticket, with the description and contact details the detail screen shows.
/// </summary>
public sealed class TicketDetailDto
{
    public int Id { get; set; }

    public string Reference { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public TicketStatus Status { get; set; }

    public TicketPriority Priority { get; set; }

    public CustomerContactDto Customer { get; set; } = null!;

    public CategoryDto Category { get; set; } = null!;

    public AgentSummaryDto? AssignedAgent { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? DueAtUtc { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    /// <summary>Derived from the dates above; never stored.</summary>
    public SlaStatus SlaStatus { get; set; }
    public TicketTriageDto? Triage { get; set; }
}

public sealed record TicketTriageDto(string PriorityReason, string SlaWindowReason, string AssignmentReason);
public sealed record AssignmentCandidate(int Id, string FullName, bool IsActive, int OpenTicketCount, int MaxOpenTickets, bool HasSpecialization);
public sealed record EscalateTicketResponse(TicketDetailDto Ticket, TicketEscalationDto Escalation);

public sealed class TicketEscalationDto
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public TicketPriority FromPriority { get; set; }
    public TicketPriority ToPriority { get; set; }
    public AgentSummaryDto? FromAgent { get; set; }
    public AgentSummaryDto? ToAgent { get; set; }
    public DateTime? FromDueAtUtc { get; set; }
    public DateTime? ToDueAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string EscalatedBy { get; set; } = string.Empty;
    public DateTime EscalatedAtUtc { get; set; }
}
