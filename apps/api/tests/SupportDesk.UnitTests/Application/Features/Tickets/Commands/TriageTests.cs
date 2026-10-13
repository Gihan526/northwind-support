using SupportDesk.Application.Abstractions;
using SupportDesk.Application.Contracts.Tickets;
using SupportDesk.Application.Features.Tickets.Commands.EscalateTicket;
using SupportDesk.Application.Services;
using SupportDesk.Domain.Aggregates.Categories;
using SupportDesk.Domain.Aggregates.Customers;
using SupportDesk.Domain.Aggregates.Tickets;
using SupportDesk.Domain.Exceptions;
using SupportDesk.UnitTests.TestDoubles;

namespace SupportDesk.UnitTests.Application.Features.Tickets.Commands;

public class TriageTests
{
    [Theory]
    [InlineData(TicketPriority.Low, CustomerTier.Standard, 72)]
    [InlineData(TicketPriority.Medium, CustomerTier.Standard, 24)]
    [InlineData(TicketPriority.High, CustomerTier.Standard, 8)]
    [InlineData(TicketPriority.Critical, CustomerTier.Standard, 4)]
    [InlineData(TicketPriority.Low, CustomerTier.Premium, 36)]
    [InlineData(TicketPriority.Medium, CustomerTier.Premium, 12)]
    [InlineData(TicketPriority.High, CustomerTier.Premium, 4)]
    [InlineData(TicketPriority.Critical, CustomerTier.Premium, 2)]
    public void Window_FollowsPriorityAndTier(TicketPriority priority, CustomerTier tier, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), new TicketTriageService(Mock.Of<ITicketQueries>(), new SlaSettings()).CalculateWindow(priority, tier));

    [Fact]
    public void Window_UsesConfiguredMultiplierAndFloor()
    {
        var settings = new SlaSettings { PremiumCustomerMultiplier = 0.1 };
        settings.BaseWindowHours[TicketPriority.High] = 10;
        var service = new TicketTriageService(Mock.Of<ITicketQueries>(), settings);
        Assert.Equal(TimeSpan.FromHours(10), service.CalculateWindow(TicketPriority.High, CustomerTier.Standard));
        Assert.Equal(TimeSpan.FromHours(1), service.CalculateWindow(TicketPriority.Critical, CustomerTier.Premium));
        settings.MinimumWindowHours = 2;
        Assert.Equal(TimeSpan.FromHours(2), service.CalculateWindow(TicketPriority.High, CustomerTier.Premium));
    }

    [Theory]
    [InlineData(null, false, TicketPriority.Medium)]
    [InlineData(TicketPriority.High, false, TicketPriority.High)]
    [InlineData(TicketPriority.Low, true, TicketPriority.Critical)]
    public async Task Create_UsesDefaultRequestedOrFlaggedPriority(TicketPriority? requested, bool forced, TicketPriority expected)
    {
        var context = new TicketCommandTestContext();
        context.Categories.Setup(r => r.GetByIdAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Category("Name not hardcoded in the rules", false, forced));
        var result = await context.RaiseTicket.HandleAsync(new CreateTicketRequest("Login broken", "Every user cannot log in", 1, 3, requested), default);
        Assert.Equal(expected, context.AddedTicket!.Priority);
        Assert.NotNull(context.AddedTicket.DueAtUtc);
        Assert.Null(context.AddedTicket.AssignedAgentId);
        Assert.Contains("Unassigned because", result.Triage!.AssignmentReason);
        context.VerifySaved(Times.Once());
    }

    [Fact]
    public void Assignment_UsesEligibilityWorkloadAndId()
    {
        AssignmentCandidate[] agents = [new(1, "Inactive", false, 0, 5, true), new(2, "Unqualified", true, 0, 5, false),
            new(3, "Full", true, 5, 5, true), new(4, "Busy", true, 3, 5, true), new(6, "Tie", true, 1, 5, true), new(5, "Winner", true, 1, 5, true)];
        Assert.Equal(5, TicketTriageService.ChooseAgent(agents, true)!.Id);
        Assert.Equal(2, TicketTriageService.ChooseAgent(agents, false)!.Id);
        Assert.Equal(4, TicketTriageService.ChooseAgent(agents, true, 4)!.Id);
        Assert.Equal(5, TicketTriageService.ChooseAgent(agents, true, 1)!.Id);
    }

    [Theory]
    [InlineData(false, 0, 5, true)]
    [InlineData(true, 0, 5, false)]
    [InlineData(true, 5, 5, true)]
    [InlineData(true, 6, 5, true)]
    public void Assignment_NobodyEligible_ReturnsNull(bool active, int count, int max, bool specialist) =>
        Assert.Null(TicketTriageService.ChooseAgent([new(1, "Alex", active, count, max, specialist)], true));

    [Theory]
    [InlineData(TicketPriority.Low, TicketPriority.Medium)]
    [InlineData(TicketPriority.Medium, TicketPriority.High)]
    [InlineData(TicketPriority.High, TicketPriority.Critical)]
    public void Escalation_OneStepFromNowWithBeforeAfterHistory(TicketPriority from, TicketPriority to)
    {
        var ticket = new TicketBuilder().WithPriority(from).Build();
        ticket.ApplyTriage(1, TimeSpan.FromHours(24), FixedClock.DefaultNow);
        var now = FixedClock.DefaultNow.AddHours(20);
        var row = ticket.Escalate(2, TimeSpan.FromHours(4), "  Urgent customer issue  ", "Alex (agent #1)", now);
        Assert.Equal(to, ticket.Priority);
        Assert.Equal(now.AddHours(4), ticket.DueAtUtc);
        Assert.Equal(FixedClock.DefaultNow, ticket.CreatedAtUtc);
        Assert.Equal(from, row.FromPriority); Assert.Equal(to, row.ToPriority);
        Assert.Equal(1, row.FromAgentId); Assert.Equal(2, row.ToAgentId);
        Assert.Equal(FixedClock.DefaultNow.AddHours(24), row.FromDueAtUtc);
        Assert.Equal(now.AddHours(4), row.ToDueAtUtc);
        Assert.Equal("Urgent customer issue", row.Reason);
        Assert.Equal("Alex (agent #1)", row.EscalatedBy);
        Assert.Equal(now, row.EscalatedAtUtc);
        if (to != TicketPriority.Critical) ticket.Escalate(null, TimeSpan.FromHours(1), "Still urgent", "Sam", now);
        Assert.Equal(to, row.ToPriority); Assert.Equal(2, row.ToAgentId);
    }

    [Theory]
    [InlineData(true, 7)]
    [InlineData(false, 8)]
    public async Task Escalation_KeepsOrReassignsOwnerAndSavesOnce(bool ownerActive, int expected)
    {
        var context = new TicketCommandTestContext();
        var ticket = context.ExistingTicket(new TicketBuilder().AssignedTo(7).Build());
        ticket.ApplyTriage(7, TimeSpan.FromHours(12), context.Clock.UtcNow.AddHours(-10));
        context.TicketQueries.Setup(q => q.GetAssignmentCandidatesAsync(It.IsAny<int>(), ticket.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new AssignmentCandidate(7, "Owner", ownerActive, 4, 5, true), new AssignmentCandidate(8, "Free", true, 0, 5, true) });
        await context.EscalateTicket.HandleAsync(ticket.Id, new EscalateTicketRequest("More urgent now"), "Alex", default);
        Assert.Equal(expected, ticket.AssignedAgentId);
        Assert.Equal(context.Clock.UtcNow.AddHours(4), ticket.DueAtUtc);
        Assert.NotNull(context.AddedEscalation);
        context.VerifySaved(Times.Once());
    }

    [Theory]
    [InlineData(TicketPriority.Critical, TicketStatus.New)]
    [InlineData(TicketPriority.Medium, TicketStatus.Resolved)]
    [InlineData(TicketPriority.Medium, TicketStatus.Closed)]
    public async Task Escalation_RejectsWithoutSaving(TicketPriority priority, TicketStatus status)
    {
        var context = new TicketCommandTestContext();
        var ticket = context.ExistingTicket(new TicketBuilder().WithPriority(priority).WithStatus(status).Build());
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => context.EscalateTicket.HandleAsync(ticket.Id,
            new EscalateTicketRequest("More urgent now"), "Alex", default));
        Assert.Equal(priority, ticket.Priority);
        Assert.Null(context.AddedEscalation);
        context.VerifySaved(Times.Never());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(" abc ", false)]
    [InlineData(" abcde ", true)]
    public void ReasonValidation_UsesTrimmedLength(string? reason, bool valid) =>
        Assert.Equal(valid, new EscalateTicketRequestValidator().Validate(new EscalateTicketRequest(reason!)).IsValid);

    [Fact]
    public void ReasonValidation_UsesMaximumLength()
    {
        var validator = new EscalateTicketRequestValidator();
        Assert.True(validator.Validate(new EscalateTicketRequest(new string('x', 500))).IsValid);
        Assert.False(validator.Validate(new EscalateTicketRequest(new string('x', 501))).IsValid);
    }

    [Theory]
    [InlineData(-1, SlaStatus.WithinSla)]
    [InlineData(0, SlaStatus.AtRisk)]
    [InlineData(1, SlaStatus.AtRisk)]
    [InlineData(3600000, SlaStatus.AtRisk)]
    [InlineData(3600001, SlaStatus.Breached)]
    public void Sla_ExactRiskAndDueBoundaries(int milliseconds, SlaStatus expected)
    {
        var due = FixedClock.DefaultNow.AddHours(4);
        Assert.Equal(expected, SlaEvaluator.Evaluate(due, null, due.AddHours(-1).AddMilliseconds(milliseconds), TimeSpan.FromHours(4).TotalMilliseconds));
    }
}
