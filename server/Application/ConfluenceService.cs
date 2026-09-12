using System.Collections.Concurrent;
using System.Collections.Immutable;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application;

/// <summary>
/// C4 가중치 주입 지점 (#167). 기본은 전부 1.0(미검증)이며 K4 측정 파이프라인이 파일로 채운다.
/// </summary>
public sealed record ConfluenceWeights(string Version, ImmutableDictionary<string, double> Values)
{
    public static readonly ConfluenceWeights Default =
        new(ConfluenceAggregator.DefaultWeightsVersion, ImmutableDictionary<string, double>.Empty);
}

/// <summary>기법 하나의 공개 형태 (C3, #167).</summary>
public sealed record ConfluenceTechniqueDto(string Name, double Score, double Confidence, double Weight,
    bool Warmup, bool Contributing, string? CorrelationGroup, IReadOnlyDictionary<string, double?> Evidence);

/// <summary>관측 full 레코드에 additive로 실리는 컨플루언스 블록 (C4, #167).</summary>
public sealed record ConfluenceDto(DateTimeOffset BarEnd, double? Score, int WarmupCount, string PolicyHash,
    string WeightsVersion, ConfluenceTechniqueDto[] Techniques);

/// <summary>`/api/structure/{symbol}`에 additive로 붙는 요약 (C4, #167).</summary>
public sealed record ConfluenceSummaryDto(double? Score, int WarmupCount, string WeightsVersion);

/// <summary>`GET /api/confluence/{symbol}` 응답 (#167). 조회가 계산을 유발하지 않는다.</summary>
public sealed record ConfluenceResponse(string Symbol, string Status, string PolicyHash, string WeightsVersion,
    DateTimeOffset? BarEnd, double? Score, int WarmupCount, ConfluenceTechniqueDto[] Techniques,
    DateTimeOffset UpdatedAt);

/// <summary>
/// C1 컨플루언스 층의 Application 진입점 (#167). 완료 봉마다 기법 신호와 합산 점수를 계산해 캐시하고
/// 관측·API에 additive로 노출한다. 진입 판정·게이트에는 쓰지 않는다(1단계).
/// </summary>
public sealed class ConfluenceService(
    ILocalStore store,
    TimeProvider clock,
    ConfluencePolicy? policy = null,
    ConfluenceWeights? weights = null,
    IBenchmarkBarSource? benchmark = null)
{
    public const string StatusReady = "ready";
    public const string StatusWarmup = "warmup";

    readonly ConfluencePolicy _policy = policy ?? ConfluencePolicy.Default;
    readonly ConfluenceWeights _weights = weights ?? ConfluenceWeights.Default;
    readonly ConcurrentDictionary<string, ConfluenceScore> _scores = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, QuoteRing> _books = new(StringComparer.OrdinalIgnoreCase);

    public ConfluencePolicy Policy => _policy;
    public string PolicyHash => _policy.PolicyHash;
    public string WeightsVersion => _weights.Version;

    public void Clear() { _scores.Clear(); _books.Clear(); }

    public void Remove(string symbol) { _scores.TryRemove(symbol, out _); _books.TryRemove(symbol, out _); }

    public bool TryGet(string symbol, out ConfluenceScore score) => _scores.TryGetValue(symbol, out score!);

    public ConfluenceSummaryDto? Summary(string symbol) =>
        _scores.TryGetValue(symbol, out var score)
            ? new ConfluenceSummaryDto(score.Score, score.WarmupCount, score.WeightsVersion)
            : null;

    /// <summary>매 poll의 호가 스냅샷을 링버퍼에 남긴다. 세션이 바뀌면 이전 세션 스냅샷은 버린다.</summary>
    public void ObserveQuote(string symbol, DateTimeOffset? sessionStart, StructureLiquidity? liquidity)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (sessionStart is not { } session) return;
        if (liquidity is not { BidSize: { } bid, AskSize: { } ask, At: { } at }) return;
        if (!double.IsFinite(bid) || !double.IsFinite(ask) || bid < 0 || ask < 0 || bid + ask <= 0) return;

        var window = Math.Max(1, _policy.OrderBookPollWindow);
        _books.AddOrUpdate(symbol,
            _ => new QuoteRing(session, [new OrderBookSnapshot(at, bid, ask)]),
            (_, current) =>
            {
                var snapshots = current.SessionStart == session ? current.Snapshots : [];
                if (snapshots.Length > 0 && snapshots[^1].ObservedAt == at) return new QuoteRing(session, snapshots);
                var next = snapshots.Add(new OrderBookSnapshot(at, bid, ask));
                if (next.Length > window) next = next.RemoveRange(0, next.Length - window);
                return new QuoteRing(session, next);
            });
    }

    /// <summary>
    /// 완료 봉 하나의 컨플루언스 점수. 상대거래량은 기존 §16B 계산 경로를 그대로 재사용한다.
    /// 계산에 쓸 봉이 없으면 null을 돌려주고 캐시를 건드리지 않는다.
    /// </summary>
    public ConfluenceScore? Evaluate(string symbol, DateTimeOffset sessionStart,
        ImmutableArray<StructureBar> bars, IReadOnlyList<Candle>? dailyBars)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (bars.Length == 0) return null;

        var lastBar = bars[^1];
        var relativeVolume = SessionIndicators.RelativeVolume(bars, lastBar.Start, _policy.RelativeVolumeLookbackBars);
        var input = new ConfluenceInput(symbol, sessionStart, Convert(bars), BenchmarkBars(sessionStart),
            Book(symbol, sessionStart), relativeVolume, PreviousSessionClose(dailyBars, sessionStart));

        var signals = ConfluenceTechniques.Evaluate(input, _policy);
        var score = ConfluenceAggregator.Aggregate(symbol, lastBar.End, signals, _policy, _weights.Values,
            _weights.Version);
        _scores[symbol] = score;
        return score;
    }

    public async Task<(int HttpStatus, ConfluenceResponse? Response)> GetAsync(string input, CancellationToken ct)
    {
        var symbol = (input ?? string.Empty).Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, "^[A-Z0-9.-]{1,12}$")) return (400, null);
        var watch = await store.Read("watchlist.json", new List<WatchItem>());
        if (watch.All(x => !x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) return (404, null);

        var now = clock.GetLocalNow();
        if (!_scores.TryGetValue(symbol, out var score))
            return (200, new ConfluenceResponse(symbol, StatusWarmup, PolicyHash, WeightsVersion, null, null, 0,
                [], now));
        return (200, new ConfluenceResponse(symbol, StatusReady, score.PolicyHash, score.WeightsVersion,
            score.BarEnd, score.Score, score.WarmupCount, Techniques(score), now));
    }

    public static ConfluenceDto? Dto(ConfluenceScore? score) =>
        score is null
            ? null
            : new ConfluenceDto(score.BarEnd, score.Score, score.WarmupCount, score.PolicyHash, score.WeightsVersion,
                Techniques(score));

    static ConfluenceTechniqueDto[] Techniques(ConfluenceScore score) =>
        score.Contributing
            .Select(x => new ConfluenceTechniqueDto(x.Name, x.Score, x.Confidence, x.Weight, x.Warmup,
                x.Contributing, x.CorrelationGroup, x.Evidence))
            .ToArray();

    ImmutableArray<OrderBookSnapshot> Book(string symbol, DateTimeOffset sessionStart) =>
        _books.TryGetValue(symbol, out var ring) && ring.SessionStart == sessionStart
            ? ring.Snapshots
            : ImmutableArray<OrderBookSnapshot>.Empty;

    /// <summary>당일 벤치마크 완료 봉. 소스가 없거나 당일 봉이 없으면 비어 있고 RS는 c=0이 된다(C3).</summary>
    ImmutableArray<IndicatorBar> BenchmarkBars(DateTimeOffset sessionStart)
    {
        if (benchmark is null) return ImmutableArray<IndicatorBar>.Empty;
        var bars = benchmark.Bars;
        if (bars.Count == 0) return ImmutableArray<IndicatorBar>.Empty;
        var result = ImmutableArray.CreateBuilder<IndicatorBar>(bars.Count);
        foreach (var bar in bars.Where(x => x.Timestamp >= sessionStart).OrderBy(x => x.Timestamp))
            result.Add(new IndicatorBar(bar.Timestamp, bar.Timestamp.AddMinutes(1), (decimal)bar.Open,
                (decimal)bar.High, (decimal)bar.Low, (decimal)bar.Close, (decimal)bar.Volume));
        return result.ToImmutable();
    }

    static ImmutableArray<IndicatorBar> Convert(ImmutableArray<StructureBar> bars)
    {
        var result = ImmutableArray.CreateBuilder<IndicatorBar>(bars.Length);
        foreach (var bar in bars)
            result.Add(new IndicatorBar(bar.Start, bar.End, bar.Open, bar.High, bar.Low, bar.Close,
                (decimal)bar.Volume));
        return result.ToImmutable();
    }

    /// <summary>C2 ATR 세션 첫봉 TR의 기준. 세션 시작 이전의 마지막 일봉 종가이며 없으면 null이다.</summary>
    static decimal? PreviousSessionClose(IReadOnlyList<Candle>? dailyBars, DateTimeOffset sessionStart)
    {
        if (dailyBars is null || dailyBars.Count == 0) return null;
        var previous = dailyBars.Where(x => x.Timestamp < sessionStart).OrderBy(x => x.Timestamp).LastOrDefault();
        return previous is null || !double.IsFinite(previous.Close) || previous.Close <= 0
            ? null
            : (decimal)previous.Close;
    }

    sealed record QuoteRing(DateTimeOffset SessionStart, ImmutableArray<OrderBookSnapshot> Snapshots);
}
