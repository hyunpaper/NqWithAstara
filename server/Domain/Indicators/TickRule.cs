using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>틱룰 방향. 세션 첫 체결은 Undetermined이다 (C2, #166).</summary>
public enum TickDirection
{
    Undetermined,
    Down,
    Flat,
    Up,
}

/// <summary>틱룰 입력 체결 (C2, #166).</summary>
public sealed record TickTrade(DateTimeOffset At, decimal Price);

/// <summary>틱룰: 직전가 대비 |Δ| &lt; 0.005면 보합, 세션 첫 체결은 방향 미정 (C2, #166).</summary>
public static class TickRule
{
    public const decimal FlatThreshold = 0.005m;

    public static TickDirection Classify(decimal? previousPrice, decimal price)
    {
        if (previousPrice is not { } previous) return TickDirection.Undetermined;
        var delta = price - previous;
        if (Math.Abs(delta) < FlatThreshold) return TickDirection.Flat;
        return delta > 0 ? TickDirection.Up : TickDirection.Down;
    }

    /// <summary>방향의 부호값. 보합은 0, 미정은 null이다 (C2, #166).</summary>
    public static int? Sign(TickDirection direction) => direction switch
    {
        TickDirection.Up => 1,
        TickDirection.Down => -1,
        TickDirection.Flat => 0,
        _ => null,
    };

    public static ImmutableArray<TickDirection> Series(IReadOnlyList<TickTrade> sessionTrades)
    {
        ArgumentNullException.ThrowIfNull(sessionTrades);
        var result = ImmutableArray.CreateBuilder<TickDirection>(sessionTrades.Count);
        decimal? previous = null;
        foreach (var trade in sessionTrades)
        {
            result.Add(Classify(previous, trade.Price));
            previous = trade.Price;
        }
        return result.ToImmutable();
    }
}
