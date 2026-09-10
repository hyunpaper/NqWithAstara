namespace Astra.Server.Domain;

public sealed record SetupLatch(DateTimeOffset At, DateTimeOffset TriggerBarAt, double TriggerLow, string Kind, DateTimeOffset? SessionStart, bool Invalidated = false);
public sealed record BreakoutLatch(DateTimeOffset At, DateTimeOffset TriggerBarAt, string Label, double Price, DateTimeOffset? SessionStart);
public sealed record SetupTransition(SetupLatch? State, string? Display, DateTimeOffset? DisplayAt, bool Emit);
public sealed record BreakoutTransition(BreakoutLatch? State, string? Display, DateTimeOffset? DisplayAt, bool Emit);

public static class SignalLifecycle
{
    public static SetupTransition UpdateSetup(SetupLatch? prior, string? detected, Candle triggerBar, double livePrice,
        int score, double vwap, DateTimeOffset now, DateTimeOffset? sessionStart, bool allowEntry)
    {
        if (prior?.SessionStart != sessionStart) prior = null;
        if (prior is { Invalidated: true })
        {
            if (triggerBar.Timestamp <= prior.TriggerBarAt) return new(prior, null, null, false);
            prior = null;
        }
        var invalid = prior is not null && (now - prior.At > TimeSpan.FromMinutes(5) ||
            Math.Min(triggerBar.Close, livePrice) < prior.TriggerLow || prior.Kind == "SETUP" && score < 60);
        if (invalid)
        {
            // Keep a tombstone regardless of the detector's current result. A later poll of
            // this candle must not resurrect a latch after an invalidating price was observed.
            return new(prior! with { Invalidated = true }, null, null, false);
        }

        if (detected == "CHASE") return new(prior, "CHASE", prior?.At, false);

        if (detected is "SETUP" or "REBOUND")
        {
            if (prior is not null && prior.Kind == detected)
            {
                return new(prior, detected, prior.At, false);
            }
            if (livePrice < triggerBar.Low || detected == "SETUP" && score < 70)
                return new(new SetupLatch(now, triggerBar.Timestamp, triggerBar.Low, detected, sessionStart, true), null, null, false);
            var next = new SetupLatch(now, triggerBar.Timestamp, triggerBar.Low, detected, sessionStart);
            return new(next, detected, now, allowEntry);
        }
        if (prior is null) return new(null, detected == "CHASE" ? "CHASE" : null, null, false);
        var display = prior.Kind == "REBOUND" ? "REBOUND" : score >= 70 && triggerBar.Close >= vwap ? "SETUP" : "SETUP_WEAK";
        return new(prior, display, prior.At, false);
    }

    public static BreakoutTransition UpdateBreakout(BreakoutLatch? prior, PriceLevel? hit, Candle triggerBar,
        double livePrice, DateTimeOffset now, DateTimeOffset? sessionStart, bool allowEntry)
    {
        if (prior?.SessionStart != sessionStart) prior = null;
        var duplicate = prior is not null && prior.Label == hit?.Label && Math.Abs(prior.Price - hit.Price) <= .0001 &&
                        (triggerBar.Timestamp <= prior.TriggerBarAt || now - prior.At <= TimeSpan.FromMinutes(30));
        var emit = false;
        if (hit is not null && !duplicate)
        {
            prior = new(now, triggerBar.Timestamp, hit.Label, hit.Price, sessionStart);
            emit = allowEntry && livePrice > hit.Price;
        }
        if (prior is null || now - prior.At > TimeSpan.FromMinutes(10) ||
            triggerBar.Close <= prior.Price || livePrice <= prior.Price)
            return new(prior, null, null, emit);
        return new(prior, $"{prior.Label} {prior.Price:0.##}", prior.At, emit);
    }
}
