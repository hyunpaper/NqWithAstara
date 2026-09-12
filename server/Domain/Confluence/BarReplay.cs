using System.Collections.Immutable;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Confluence;

/// <summary>
/// 재생 창 한 개 (C5·C6, #169). 현재 봉까지의 슬라이스만 담으며 이후 봉은 이 타입 안에 존재하지 않는다.
/// </summary>
public sealed record BarReplayWindow(
    string Symbol,
    DateTimeOffset SessionStart,
    int Index,
    ImmutableArray<IndicatorBar> Completed,
    ImmutableArray<IndicatorBar> Benchmark)
{
    /// <summary>평가 대상(마지막 완료) 봉.</summary>
    public IndicatorBar Current => Completed[^1];
}

/// <summary>
/// 저장 봉 순차 재생 (C5·C6, #169). 창은 0..i 슬라이스를 새로 만들어 넘기므로 미래 봉 접근 경로가 없다.
/// 벤치마크도 현재 봉 종료 시각 이하만 잘라 넘긴다.
/// </summary>
public static class SequentialBarReplay
{
    public static IEnumerable<BarReplayWindow> Windows(string symbol, DateTimeOffset sessionStart,
        IReadOnlyList<IndicatorBar> bars, IReadOnlyList<IndicatorBar>? benchmark = null)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(bars);

        var ordered = bars.OrderBy(x => x.End).ToArray();
        var benchmarkOrdered = (benchmark ?? []).OrderBy(x => x.End).ToArray();
        var benchmarkIndex = 0;
        var completed = ImmutableArray.CreateBuilder<IndicatorBar>(ordered.Length);
        var benchmarkSlice = ImmutableArray.CreateBuilder<IndicatorBar>(benchmarkOrdered.Length);

        for (var i = 0; i < ordered.Length; i++)
        {
            completed.Add(ordered[i]);
            while (benchmarkIndex < benchmarkOrdered.Length && benchmarkOrdered[benchmarkIndex].End <= ordered[i].End)
                benchmarkSlice.Add(benchmarkOrdered[benchmarkIndex++]);
            yield return new BarReplayWindow(symbol, sessionStart, i, completed.ToImmutable(),
                benchmarkSlice.ToImmutable());
        }
    }
}
