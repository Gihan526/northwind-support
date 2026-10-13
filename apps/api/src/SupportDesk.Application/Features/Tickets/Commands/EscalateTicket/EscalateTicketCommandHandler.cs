using FluentValidation;
using SupportDesk.Application.Abstractions;
using SupportDesk.Application.Contracts.Tickets;
using SupportDesk.Application.Exceptions;
using SupportDesk.Application.Features.Tickets.Queries.GetTicket;
using SupportDesk.Application.Services;
using SupportDesk.Domain.Repositories;

namespace SupportDesk.Application.Features.Tickets.Commands.EscalateTicket;

public sealed class EscalateTicketCommandHandler(ITicketRepository tickets, ICustomerRepository customers,
    ICategoryRepository categories, IUnitOfWork unitOfWork, IClock clock, TicketTriageService triage,
    GetTicketQueryHandler getTicket, ITicketQueries queries)
{
    public async Task<EscalateTicketResponse> HandleAsync(int id, EscalateTicketRequest request, string actor, CancellationToken ct)
    {
        var ticket = await tickets.GetByIdAsync(id, ct) ?? throw new NotFoundException("Ticket", id);
        ticket.GetEscalatedPriority(); // Check conflicts before doing more queries or changing anything.
        var customer = await customers.GetByIdAsync(ticket.CustomerId, ct) ?? throw new NotFoundException("Customer", ticket.CustomerId);
        var category = await categories.GetByIdAsync(ticket.CategoryId, ct) ?? throw new NotFoundException("Category", ticket.CategoryId);
        var decision = await triage.ForEscalationAsync(ticket, customer, category, ct);
        var row = ticket.Escalate(decision.AgentId, decision.Window, request.Reason, actor, clock.UtcNow);
        await tickets.AddEscalationAsync(row, ct);
        await unitOfWork.SaveChangesAsync(ct); // Ticket and history save in one transaction.
        var result = await getTicket.HandleAsync(id, ct);
        result.Triage = decision.Explanation;
        var history = await queries.GetEscalationsAsync(id, ct);
        return new EscalateTicketResponse(result, history.First(item => item.Id == row.Id));
    }
}

public sealed class EscalateTicketRequestValidator : AbstractValidator<EscalateTicketRequest>
{
    public EscalateTicketRequestValidator() => RuleFor(request => request.Reason)
        .Must(reason => reason is not null && reason.Trim().Length >= 5 && reason.Trim().Length <= 500)
        .WithMessage("Reason must contain 5 to 500 characters after trimming.");
}
