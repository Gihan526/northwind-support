using Microsoft.EntityFrameworkCore;
using SupportDesk.Domain.Aggregates.Tickets;
using SupportDesk.Domain.Repositories;
using SupportDesk.Infrastructure.Data;

namespace SupportDesk.Infrastructure.Repositories;

/// <summary>
/// Loads and stores <see cref="Ticket"/> aggregates. Loaded tickets are tracked, so the unit
/// of work saves whatever their methods changed.
/// </summary>
public sealed class TicketRepository(SupportDbContext db) : ITicketRepository
{
    public Task<Ticket?> GetByIdAsync(int id, CancellationToken ct) =>
        db.Set<Ticket>().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<string> NextReferenceAsync(CancellationToken ct)
    {
        // Single-instance application: the highest id so far is good enough. A database
        // sequence would be the answer if more than one API instance created tickets.
        var lastId = await db.Set<Ticket>()
            .AsNoTracking()
            .OrderByDescending(t => t.Id)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);

        return $"TCK-{lastId + 1:D4}";
    }

    public async Task AddAsync(Ticket ticket, CancellationToken ct) =>
        await db.Set<Ticket>().AddAsync(ticket, ct);
    public async Task AddEscalationAsync(TicketEscalation row, CancellationToken ct) =>
        await db.Set<TicketEscalation>().AddAsync(row, ct);
}
