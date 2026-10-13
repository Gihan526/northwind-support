using SupportDesk.Domain.Common;

namespace SupportDesk.Domain.Aggregates.Tickets;

// Immutable snapshot: no public setters or editing methods.
public sealed class TicketEscalation : Entity
{
    private TicketEscalation() { }
    public TicketEscalation(int ticketId, TicketPriority fromPriority, TicketPriority toPriority,
        int? fromAgentId, int? toAgentId, DateTime? fromDueAtUtc, DateTime? toDueAtUtc,
        string reason, string actor, DateTime now)
    {
        reason = reason.Trim();
        actor = actor.Trim();
        if (reason.Length < 5 || reason.Length > 500) throw new ArgumentException("Reason must contain 5 to 500 characters.", nameof(reason));
        if (actor.Length < 1 || actor.Length > 100) throw new ArgumentException("Actor must contain 1 to 100 characters.", nameof(actor));
        TicketId = ticketId;
        FromPriority = fromPriority;
        ToPriority = toPriority;
        FromAgentId = fromAgentId;
        ToAgentId = toAgentId;
        FromDueAtUtc = fromDueAtUtc;
        ToDueAtUtc = toDueAtUtc;
        Reason = reason;
        EscalatedBy = actor;
        EscalatedAtUtc = now;
    }
    public int TicketId { get; private set; }
    public TicketPriority FromPriority { get; private set; }
    public TicketPriority ToPriority { get; private set; }
    public int? FromAgentId { get; private set; }
    public int? ToAgentId { get; private set; }
    public DateTime? FromDueAtUtc { get; private set; }
    public DateTime? ToDueAtUtc { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string EscalatedBy { get; private set; } = string.Empty;
    public DateTime EscalatedAtUtc { get; private set; }
}
