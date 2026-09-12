using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

/// <summary>측정 한 번의 요약 (C5, #169). 지평별 기법 통계를 전부 담는다.</summary>
public sealed record ConfluenceMeasurementReport(DateOnly From, DateOnly To, int Days, int Symbols, int Bars,
    int Signals, int HorizonBars, ImmutableArray<TechniqueMeasurement> Techniques,
    ImmutableDictionary<int, ImmutableArray<TechniqueMeasurement>> ByHorizon);

/// <summary>
/// 저장 봉 재생 러너 (C5, #169). `App_Data/bars/&lt;날짜&gt;/&lt;심볼&gt;.jsonl`을 날짜·심볼별로 시간순 재생하며
/// 각 완료 봉에서 K2 기법 신호를 계산한다. 신호는 <see cref="SequentialBarReplay"/> 슬라이스만 보고,
/// 미래 봉은 결과(N봉 후 수익) 계산에서만 읽는다. 호가는 저장되지 않으므로 OBI는 항상 c=0이다.
/// </summary>
public sealed class ConfluenceReplay(IBarStore store, ConfluencePolicy? policy = null,
    MeasurementPolicy? measurement = null)
{
    readonly ConfluencePolicy _policy = policy ?? ConfluencePolicy.Default;
    readonly MeasurementPolicy _measurement = measurement ?? MeasurementPolicy.Default;

    public MeasurementPolicy Measurement => _measurement;

    public async Task<ConfluenceMeasurementReport> RunAsync(DateOnly from, DateOnly to, int horizonBars,
        string benchmarkSymbol, CancellationToken ct)
    {
        if (horizonBars < 1) throw new ArgumentOutOfRangeException(nameof(horizonBars));
        var days = ConfluenceWalkForward.DaysInWindow(await store.ListDaysAsync(ct), from, to);
        var outcomes = new List<SignalOutcome>();
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int barCount = 0, signalCount = 0;

        foreach (var day in days)
        {
            var key = day.ToString("yyyy-MM-dd");
            var benchmark = await LoadAsync(key, benchmarkSymbol, ct);
            foreach (var symbol in await store.ListSymbolsAsync(key, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(symbol, benchmarkSymbol, StringComparison.OrdinalIgnoreCase)) continue;
                var bars = await LoadAsync(key, symbol, ct);
                if (bars.Length == 0) continue;
                symbols.Add(symbol);
                barCount += bars.Length;
                signalCount += Replay(symbol, bars, benchmark, outcomes);
            }
        }

        var byHorizon = _measurement.Horizons
            .ToImmutableDictionary(x => x, x => ConfluenceMeasurement.Summarize(outcomes, x, _measurement));
        var primary = byHorizon.TryGetValue(horizonBars, out var found)
            ? found
            : ConfluenceMeasurement.Summarize(outcomes, horizonBars, _measurement);
        return new ConfluenceMeasurementReport(from, to, days.Length, symbols.Count, barCount, signalCount,
            horizonBars, primary, byHorizon.SetItem(horizonBars, primary));
    }

    /// <summary>하루·한 심볼 재생. 창마다 기법을 평가하고 발생한 신호의 지평별 결과를 모은다.</summary>
    int Replay(string symbol, ImmutableArray<IndicatorBar> bars, ImmutableArray<IndicatorBar> benchmark,
        List<SignalOutcome> outcomes)
    {
        var sessionStart = bars[0].Start;
        var signals = 0;
        foreach (var window in SequentialBarReplay.Windows(symbol, sessionStart, bars, benchmark))
        {
            var input = new ConfluenceInput(symbol, sessionStart, window.Completed, window.Benchmark,
                ImmutableArray<OrderBookSnapshot>.Empty, RelativeVolume(window.Completed), null);
            var evaluated = ConfluenceTechniques.Evaluate(input, _policy);
            var atr = AverageTrueRange.Series(window.Completed, null, _policy.AtrPeriod)[^1].Value;
            if (atr is not { } range) continue;

            foreach (var signal in evaluated)
            {
                if (!ConfluenceMeasurement.IsSignal(signal, _measurement)) continue;
                signals++;
                foreach (var horizon in _measurement.Horizons)
                {
                    var outcome = ConfluenceMeasurement.Measure(signal.Name, symbol, bars, window.Index, horizon,
                        signal.Score, range, _measurement);
                    if (outcome is not null) outcomes.Add(outcome);
                }
            }
        }
        return signals;
    }

    /// <summary>§16B 봉 단위 RVOL 계산 경로를 그대로 재사용한다(C3 보충). 창 밖 봉은 넘기지 않는다.</summary>
    double? RelativeVolume(ImmutableArray<IndicatorBar> window)
    {
        var converted = window
            .Select(x => new StructureBar(x.Start, x.End, x.Open, x.High, x.Low, x.Close, (double)x.Volume))
            .ToArray();
        return SessionIndicators.RelativeVolume(converted, converted[^1].Start, _policy.RelativeVolumeLookbackBars);
    }

    public async Task<ImmutableArray<IndicatorBar>> LoadAsync(string day, string symbol, CancellationToken ct) =>
        Parse(await store.ReadLinesAsync(day, symbol, ct));

    /// <summary>K0 저장 스키마 `{t,o,h,l,c,v}` 한 줄 → 완료 1분봉. 깨진 줄은 건너뛴다.</summary>
    public static ImmutableArray<IndicatorBar> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = ImmutableArray.CreateBuilder<IndicatorBar>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var root = JsonDocument.Parse(line).RootElement;
                var start = root.GetProperty("t").GetDateTimeOffset();
                result.Add(new IndicatorBar(start, start.AddMinutes(1), Decimal(root, "o"), Decimal(root, "h"),
                    Decimal(root, "l"), Decimal(root, "c"), Decimal(root, "v")));
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException
                                                  or InvalidOperationException or OverflowException)
            {
            }
        }
        result.Sort((a, b) => a.End.CompareTo(b.End));
        return result.ToImmutable();
    }

    static decimal Decimal(JsonElement root, string name) => (decimal)root.GetProperty(name).GetDouble();
}
