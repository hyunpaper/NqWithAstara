namespace Astra.Server.Domain.Validation;

// 이슈 #131 — 실계좌 체결과 v5 관측의 대조(Domain 절반).
// 순수 함수다. 시계를 조회하지 않고, 파일을 읽지 않으며, 입력에 없는 값을 0·false로 만들지 않는다(§11).
// 계좌번호·주문 식별자·금액 합계는 입력 계약에 아예 없다 — 대조에 필요하지 않기 때문이다.

/// <summary>실계좌 체결 한 건. 계좌·주문 식별자는 입력에 포함하지 않는다(#131).</summary>
public sealed record RealFill(string Symbol, string Side, DateTimeOffset FilledAt, decimal AverageFilledPrice,
    decimal FilledQuantity, decimal Commission, string OrderType);

/// <summary>대조에 쓰는 v5 관측 후보 한 건의 평탄한 입력 행(#131).</summary>
public sealed record RealFillCandidateRow(string Symbol, DateTimeOffset ObservedAt, string EventId, string Kind,
    string State, decimal? EntryReference, double? Atr1m, string? TrendState, double? EntryQuality,
    IReadOnlyList<string> RejectionCodes);

/// <summary>실매도 위치 판정에 쓰는 v5 거래 구간. <paramref name="ExitAt"/>가 null이면 아직 열려 있다(#131).</summary>
public sealed record RealFillOpenTrade(string Symbol, DateTimeOffset EnteredAt, DateTimeOffset? ExitAt,
    decimal Stop, decimal Target);

/// <summary>실체결 한 건의 분류. 문자열을 흩어 쓰지 않고 한곳에서 정의한다(#131).</summary>
public static class RealFillClasses
{
    /// <summary>실매수 시각 창에 v5 READY/ENTERED 후보가 있었던 건.</summary>
    public const string Matched = "MATCHED";

    /// <summary>v5는 ENTERED인데 같은 창에 실매수가 없는 건.</summary>
    public const string EngineOnly = "ENGINE_ONLY";

    /// <summary>실매수는 있는데 v5 후보가 없거나 REJECTED뿐인 건.</summary>
    public const string UserOnly = "USER_ONLY";

    /// <summary>실매도. 매칭률 분모에 들어가지 않고 stop/target 대비 위치만 본다.</summary>
    public const string Sell = "SELL";
}

/// <summary>실매도 체결가의 v5 stop/target 대비 위치(#131).</summary>
public static class RealFillSellPositions
{
    public const string AboveTarget = "ABOVE_TARGET";
    public const string BetweenStopAndTarget = "BETWEEN";
    public const string BelowStop = "BELOW_STOP";
}

/// <summary>v5 후보 상태 문자열. 관측에 저장된 대문자 표기를 그대로 쓴다(#131).</summary>
public static class RealFillCandidateStates
{
    public const string Ready = "READY";
    public const string Entered = "ENTERED";
    public const string Rejected = "REJECTED";
}

/// <summary>대조 결과 한 행. 알 수 없는 값은 null이며 0으로 대체하지 않는다(#131).</summary>
public sealed record RealFillComparison(string Classification, string Symbol, string Side, DateTimeOffset At,
    decimal? FillPrice, decimal? FillQuantity, string? EventId, string? V5State, string? Kind, string? TrendState,
    double? EntryQuality, decimal? EntryReference, double? DeviationAtr, string? SellPosition,
    IReadOnlyList<string> RejectionCodes);

/// <summary>거절 코드 빈도. 사용자 단독 진입의 상위 사유를 세기 위한 것이다(#131).</summary>
public sealed record RealFillRejectionCount(string Code, int Count);

/// <summary>
/// 하루치 대조 보고. 표본이 없으면 비율·분위는 null이며 0%로 표기하지 않는다(#131).
/// </summary>
public sealed record RealVsV5Report(DateOnly TradingDate, int WindowMinutes, int RealBuys, int RealSells,
    int Matched, int EngineOnly, int UserOnly, double? MatchRatePercent, int DeviationSamples,
    double? DeviationMedianAtr, double? DeviationQ1Atr, double? DeviationQ3Atr,
    IReadOnlyList<RealFillRejectionCount> TopUserOnlyRejections, IReadOnlyList<RealFillComparison> Rows);

/// <summary>
/// 실체결과 v5 관측을 심볼·시각 창으로 대조한다(#131). 창 경계는 양끝 포함이다.
/// </summary>
public static class RealFillComparer
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    public const string Buy = "BUY";
    public const string Sell = "SELL";

    const int TopRejectionCodes = 5;

    public static RealVsV5Report Compare(DateOnly tradingDate, IReadOnlyList<RealFill> fills,
        IReadOnlyList<RealFillCandidateRow> candidates, IReadOnlyList<RealFillOpenTrade> trades,
        TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(trades);

        var span = window ?? DefaultWindow;
        var buys = fills.Where(x => IsSide(x.Side, Buy)).OrderBy(x => x.FilledAt).ToArray();
        var sells = fills.Where(x => IsSide(x.Side, Sell)).OrderBy(x => x.FilledAt).ToArray();

        var rows = new List<RealFillComparison>();
        var matched = 0;
        var userOnly = 0;
        foreach (var buy in buys)
        {
            var row = CompareBuy(buy, candidates, span);
            if (row.Classification == RealFillClasses.Matched) matched++; else userOnly++;
            rows.Add(row);
        }

        var engineOnly = EngineOnlyRows(buys, candidates, span);
        rows.AddRange(engineOnly);
        rows.AddRange(sells.Select(x => CompareSell(x, trades)));

        var deviations = rows
            .Where(x => x.Classification != RealFillClasses.Sell && x.DeviationAtr is not null)
            .Select(x => x.DeviationAtr!.Value).OrderBy(x => x).ToArray();

        var rejections = rows.Where(x => x.Classification == RealFillClasses.UserOnly)
            .SelectMany(x => x.RejectionCodes)
            .GroupBy(x => x, StringComparer.Ordinal)
            .Select(x => new RealFillRejectionCount(x.Key, x.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Code, StringComparer.Ordinal)
            .Take(TopRejectionCodes).ToArray();

        return new RealVsV5Report(tradingDate, (int)span.TotalMinutes, buys.Length, sells.Length, matched,
            engineOnly.Count, userOnly,
            buys.Length == 0 ? null : Math.Round(matched * 100d / buys.Length, 2),
            deviations.Length, Percentile(deviations, .5), Percentile(deviations, .25), Percentile(deviations, .75),
            rejections,
            rows.OrderBy(x => x.At).ThenBy(x => x.Symbol, StringComparer.Ordinal)
                .ThenBy(x => x.Classification, StringComparer.Ordinal).ToArray());
    }

    static RealFillComparison CompareBuy(RealFill buy, IReadOnlyList<RealFillCandidateRow> candidates, TimeSpan span)
    {
        var inWindow = InWindow(candidates, buy.Symbol, buy.FilledAt, span);
        var representative = Nearest(inWindow, buy.FilledAt, RealFillCandidateStates.Entered)
            ?? Nearest(inWindow, buy.FilledAt, RealFillCandidateStates.Ready);
        var classification = representative is null ? RealFillClasses.UserOnly : RealFillClasses.Matched;
        representative ??= Nearest(inWindow, buy.FilledAt, RealFillCandidateStates.Rejected);
        return Row(classification, buy, representative);
    }

    /// <summary>v5가 진입했는데 같은 창에 실매수가 없는 이벤트. 같은 EventId는 첫 관측 한 건으로 접는다.</summary>
    static IReadOnlyList<RealFillComparison> EngineOnlyRows(IReadOnlyList<RealFill> buys,
        IReadOnlyList<RealFillCandidateRow> candidates, TimeSpan span) =>
        candidates.Where(x => string.Equals(x.State, RealFillCandidateStates.Entered, StringComparison.Ordinal))
            .GroupBy(x => x.EventId, StringComparer.Ordinal)
            .Select(x => x.OrderBy(c => c.ObservedAt).First())
            .Where(x => !buys.Any(b => IsSameSymbol(b.Symbol, x.Symbol) && Within(b.FilledAt, x.ObservedAt, span)))
            .OrderBy(x => x.ObservedAt).ThenBy(x => x.EventId, StringComparer.Ordinal)
            .Select(x => new RealFillComparison(RealFillClasses.EngineOnly, x.Symbol, Buy, x.ObservedAt, null, null,
                x.EventId, x.State, x.Kind, x.TrendState, x.EntryQuality, x.EntryReference, null, null,
                x.RejectionCodes))
            .ToArray();

    /// <summary>실매도가 그 시점에 열려 있던 v5 거래의 stop/target 어디에 있었는지. 열린 거래가 없으면 null이다.</summary>
    static RealFillComparison CompareSell(RealFill sell, IReadOnlyList<RealFillOpenTrade> trades)
    {
        var open = trades
            .Where(x => IsSameSymbol(x.Symbol, sell.Symbol) && x.EnteredAt <= sell.FilledAt
                && (x.ExitAt is null || x.ExitAt.Value >= sell.FilledAt))
            .OrderByDescending(x => x.EnteredAt).FirstOrDefault();
        var position = open is null ? null
            : sell.AverageFilledPrice > open.Target ? RealFillSellPositions.AboveTarget
            : sell.AverageFilledPrice < open.Stop ? RealFillSellPositions.BelowStop
            : RealFillSellPositions.BetweenStopAndTarget;
        return new RealFillComparison(RealFillClasses.Sell, sell.Symbol, Sell, sell.FilledAt,
            sell.AverageFilledPrice, sell.FilledQuantity, null, null, null, null, null, null, null, position, []);
    }

    static RealFillComparison Row(string classification, RealFill buy, RealFillCandidateRow? candidate) =>
        new(classification, buy.Symbol, Buy, buy.FilledAt, buy.AverageFilledPrice, buy.FilledQuantity,
            candidate?.EventId, candidate?.State, candidate?.Kind, candidate?.TrendState, candidate?.EntryQuality,
            candidate?.EntryReference, Deviation(buy.AverageFilledPrice, candidate), null,
            candidate?.RejectionCodes ?? []);

    /// <summary>체결가와 entryReference의 차이를 ATR1m 단위로. ATR이 없거나 0 이하면 null이다(0으로 바꾸지 않는다).</summary>
    static double? Deviation(decimal price, RealFillCandidateRow? candidate)
    {
        if (candidate?.EntryReference is not { } reference) return null;
        if (candidate.Atr1m is not { } atr || !double.IsFinite(atr) || atr <= 0) return null;
        var value = (double)(price - reference) / atr;
        return double.IsFinite(value) ? Math.Round(value, 4) : null;
    }

    static IReadOnlyList<RealFillCandidateRow> InWindow(IReadOnlyList<RealFillCandidateRow> candidates, string symbol,
        DateTimeOffset at, TimeSpan span) =>
        candidates.Where(x => IsSameSymbol(x.Symbol, symbol) && Within(at, x.ObservedAt, span)).ToArray();

    static RealFillCandidateRow? Nearest(IReadOnlyList<RealFillCandidateRow> candidates, DateTimeOffset at,
        string state) =>
        candidates.Where(x => string.Equals(x.State, state, StringComparison.Ordinal))
            .OrderBy(x => (x.ObservedAt - at).Duration()).ThenBy(x => x.EventId, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>창은 양끝을 포함한다 — 정확히 ±5분인 관측을 밖으로 밀어내지 않는다.</summary>
    static bool Within(DateTimeOffset fillAt, DateTimeOffset observedAt, TimeSpan span) =>
        (observedAt - fillAt).Duration() <= span;

    static bool IsSameSymbol(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    static bool IsSide(string side, string expected) =>
        string.Equals(side, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>정렬된 표본의 선형 보간 분위. 표본이 없으면 null이다.</summary>
    static double? Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return null;
        if (sorted.Length == 1) return Math.Round(sorted[0], 4);
        var position = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var weight = position - lower;
        return Math.Round(sorted[lower] + (sorted[upper] - sorted[lower]) * weight, 4);
    }
}
