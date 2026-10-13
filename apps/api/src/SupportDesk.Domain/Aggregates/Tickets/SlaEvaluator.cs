namespace SupportDesk.Domain.Aggregates.Tickets;

/// <summary>SLA status is derived from dates and an injected time; never stored.</summary>
public static class SlaEvaluator
{
    public static SlaStatus Evaluate(DateTime? due, DateTime? resolved, DateTime now,
        double windowMilliseconds = 0, double thresholdPercent = 25)
    {
        if (due is null) return SlaStatus.NotApplicable;
        if (resolved is not null) return resolved <= due ? SlaStatus.Met : SlaStatus.Breached;
        if (now > due) return SlaStatus.Breached;
        // Match the database's millisecond precision.
        var risk = Math.Floor(windowMilliseconds * thresholdPercent / 100);
        if (windowMilliseconds > 0 && now >= due.Value.AddMilliseconds(-risk)) return SlaStatus.AtRisk;
        return SlaStatus.WithinSla;
    }
}
