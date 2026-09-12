using System.Collections.Immutable;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Confluence;

/// <summary>신호 한 건의 N봉 후 결과 (C5, #169). 수익은 전부 ATR14 단위다.</summary>
public sealed record SignalOutcome(string Technique, string Symbol, DateTimeOffset BarEnd, double Score,
    int HorizonBars, double ReturnAtr, double NetReturnAtr, bool Hit);

/// <summary>기법 하나의 측정 상태 (C5, #169).</summary>
public enum MeasurementStatus
{
    /// <summary>표본 부족(n &lt; 최소 표본)이거나 BH 보정 미통과. 가중치는 기준선 1.0을 유지한다.</summary>
    Unverified,

    /// <summary>BH 통과 ∧ 적중률 &lt; 0.5 — 입증된 열위다. 가중치를 0~1.0으로 내린다.</summary>
    Rejected,

    /// <summary>BH 통과 ∧ 적중률 ≥ 0.5 — 입증된 우위다. 가중치를 1.0~2.0으로 올린다.</summary>
    Verified
}

/// <summary>기법 하나의 지평별 측정 결과 (C5, #169).</summary>
public sealed record TechniqueMeasurement(string Technique, int HorizonBars, int N, int Hits, double HitRate,
    ProportionInterval Ci, double Brier, double PValue, double MeanReturnAtr, double MeanNetReturnAtr,
    MeasurementStatus Status, double Weight)
{
    public string StatusText => Status switch
    {
        MeasurementStatus.Verified => "verified",
        MeasurementStatus.Rejected => "rejected",
        _ => "unverified"
    };
}

/// <summary>
/// C5 결과 측정과 재가중 규칙 (#169). 미래 봉은 결과 계산에서만 쓰이고 신호 계산에는 들어가지 않는다
/// — 신호는 <see cref="SequentialBarReplay"/>의 슬라이스만 보고 만들어진다.
/// </summary>
public static class ConfluenceMeasurement
{
    /// <summary>신호로 셀지 여부: warmup이 아니고 c&gt;0이며 |score|가 임계 이상일 때만 발생으로 센다(C5).</summary>
    public static bool IsSignal(TechniqueSignal signal, MeasurementPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(policy);
        return !signal.Warmup && signal.Confidence > 0 && double.IsFinite(signal.Score) &&
               Math.Abs(signal.Score) >= policy.SignalThreshold;
    }

    /// <summary>
    /// 신호 봉 종가 기준 N봉 후 결과. 남은 봉이 모자라거나 ATR이 없으면 null이며 표본에서 제외된다.
    /// </summary>
    public static SignalOutcome? Measure(string technique, string symbol, IReadOnlyList<IndicatorBar> dayBars,
        int signalIndex, int horizonBars, double score, double atr, MeasurementPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(technique);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(dayBars);
        ArgumentNullException.ThrowIfNull(policy);
        if (horizonBars < 1) throw new ArgumentOutOfRangeException(nameof(horizonBars));
        if (signalIndex < 0 || signalIndex >= dayBars.Count) throw new ArgumentOutOfRangeException(nameof(signalIndex));

        var exitIndex = signalIndex + horizonBars;
        if (exitIndex >= dayBars.Count) return null;
        if (!double.IsFinite(atr) || atr <= 0) return null;

        var direction = ConfluenceMath.Sign(score);
        if (direction == 0) return null;

        var entry = (double)dayBars[signalIndex].Close;
        var exit = (double)dayBars[exitIndex].Close;
        if (!double.IsFinite(entry) || entry <= 0 || !double.IsFinite(exit)) return null;

        var move = exit - entry;
        var cost = entry * policy.CostFraction;
        return new SignalOutcome(technique, symbol, dayBars[signalIndex].End, score, horizonBars,
            IndicatorRounding.Ratio(direction * move / atr),
            IndicatorRounding.Ratio((direction * move - cost) / atr),
            ConfluenceMath.Sign(move) == direction);
    }

    /// <summary>
    /// 기법 10개를 한 번에 요약한다. BH 보정도 10개 p값에 동시에 걸며, 표본이 없는 기법은 p=1로 들어간다.
    /// </summary>
    public static ImmutableArray<TechniqueMeasurement> Summarize(IEnumerable<SignalOutcome> outcomes,
        int horizonBars, MeasurementPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(policy);

        var byTechnique = outcomes
            .Where(x => x.HorizonBars == horizonBars)
            .GroupBy(x => x.Technique, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);

        var names = TechniqueNames.All;
        var rows = new (int N, int Hits, double HitRate, ProportionInterval Ci, double Brier, double PValue,
            double Mean, double MeanNet)[names.Length];
        var pValues = new double[names.Length];

        for (var i = 0; i < names.Length; i++)
        {
            var sample = byTechnique.TryGetValue(names[i], out var found) ? found : [];
            var n = sample.Length;
            var hits = sample.Count(x => x.Hit);
            var hitRate = n > 0 ? (double)hits / n : 0;
            var brier = MeasurementStatistics.Brier(
                sample.Select(x => (MeasurementStatistics.ForecastProbability(x.Score), x.Hit)).ToArray());
            var p = n > 0 ? MeasurementStatistics.BinomialTwoSidedP(hits, n) : 1;
            var ci = MeasurementStatistics.Wilson(hits, n);
            rows[i] = (n, hits, hitRate, new ProportionInterval(IndicatorRounding.Ratio(ci.Low),
                    IndicatorRounding.Ratio(ci.High)), double.IsNaN(brier) ? brier : IndicatorRounding.Ratio(brier),
                p, n > 0 ? IndicatorRounding.Ratio(sample.Average(x => x.ReturnAtr)) : 0,
                n > 0 ? IndicatorRounding.Ratio(sample.Average(x => x.NetReturnAtr)) : 0);
            pValues[i] = p;
        }

        var rejectedNull = MeasurementStatistics.BenjaminiHochberg(pValues, policy.FalseDiscoveryRate);
        var result = ImmutableArray.CreateBuilder<TechniqueMeasurement>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            var row = rows[i];
            var status = row.N < policy.MinimumSample || !rejectedNull[i] ? MeasurementStatus.Unverified
                : row.HitRate >= .5 ? MeasurementStatus.Verified
                : MeasurementStatus.Rejected;
            result.Add(new TechniqueMeasurement(names[i], horizonBars, row.N, row.Hits,
                IndicatorRounding.Ratio(row.HitRate), row.Ci, row.Brier, IndicatorRounding.Ratio(row.PValue),
                row.Mean, row.MeanNet, status, WeightOf(status, row.HitRate)));
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// 재가중 규칙: 미검증 1.0을 기준선으로 두고 입증된 기법만 clamp(1 + (적중률−0.5)×2, 0, 2)로 옮긴다.
    /// 우위는 1.0 위로, 열위는 1.0 아래로 간다.
    /// </summary>
    public static double WeightOf(MeasurementStatus status, double hitRate) =>
        status == MeasurementStatus.Unverified
            ? ConfluenceAggregator.DefaultWeight
            : IndicatorRounding.Ratio(Math.Clamp(1 + (hitRate - .5) * 2, 0, 2));
}
