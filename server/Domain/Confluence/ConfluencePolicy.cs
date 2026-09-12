using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Domain.Confluence;

/// <summary>
/// 컨플루언스 층의 정책 계약 (C3·C4, #167). <see cref="StructurePolicy"/>와 완전히 분리된 레코드이며
/// canonical JSON의 SHA-256으로 자체 <see cref="PolicyHash"/>를 만든다. 값이 바뀌면 hash가 반드시 바뀐다.
/// 초기 수치는 C2/C3 표의 표준 파라미터이며 성과로 탐색한 값이 아니다.
/// </summary>
public sealed record ConfluencePolicy
{
    public string Version { get; init; } = "confluence.1";

    /// <summary>C2 채택 변형 요약. 같은 지표라도 계산 변형이 다르면 신호가 달라지므로 hash 입력에 넣는다.</summary>
    public string IndicatorVariants { get; init; } =
        "ema:sma-seed;rsi:wilder;bb:population-sigma;vwap:hlc3-vw-sigma;adx:talib-seed";

    // ── 지표 파라미터 (C2 표) ──
    public int MacdFastPeriod { get; init; } = 12;
    public int MacdSlowPeriod { get; init; } = 26;
    public int MacdSignalPeriod { get; init; } = 9;
    public int RsiPeriod { get; init; } = 14;
    public int BollingerPeriod { get; init; } = 20;
    public double BollingerDeviations { get; init; } = 2.0;
    public int AtrPeriod { get; init; } = 14;
    public int AdxPeriod { get; init; } = 14;
    public int StochasticFastKPeriod { get; init; } = 14;
    public int StochasticSlowKPeriod { get; init; } = 3;
    public int StochasticSlowDPeriod { get; init; } = 3;
    public int DonchianPeriod { get; init; } = 20;
    public int OpeningRangeMinutes { get; init; } = 15;
    public int AtrChannelEmaPeriod { get; init; } = 20;
    public double AtrChannelAtrFactor { get; init; } = 2.0;
    public int RelativeVolumeLookbackBars { get; init; } = 20;

    /// <summary>RS 기법이 벤치마크 봉을 같은 시점으로 인정하는 최대 시각 차(C3).</summary>
    public int BenchmarkSyncToleranceSeconds { get; init; } = 60;

    /// <summary>OBI 평균에 쓰는 최근 호가 스냅샷 개수(C3).</summary>
    public int OrderBookPollWindow { get; init; } = 3;

    /// <summary>BB 스퀴즈·밴드폭 극단 판정에 쓰는 밴드폭 관측 창(C3).</summary>
    public int SqueezeLookbackBars { get; init; } = 20;

    // ── 기법 score·confidence 상수 (C3 표) ──
    public double MacdCrossBonus { get; init; } = .2;
    public double MacdBelowZeroConfidence { get; init; } = .5;
    public double TrendAdxThreshold { get; init; } = 20;
    public double RsiNonTrendConfidence { get; init; } = .7;
    public double BollingerSqueezeBonus { get; init; } = .3;
    public double BollingerExtremeConfidence { get; init; } = .6;
    public double AdxScoreScale { get; init; } = 50;
    public double AdxLowConfidence { get; init; } = .4;

    // ── 2군 기법 파라미터 (C3-2, #170) ──

    /// <summary>캔들 확인 점수 — 강세 장악형·망치·핀바 순. 성과로 탐색한 값이 아니다.</summary>
    public double CandleEngulfingScore { get; init; } = .6;
    public double CandleHammerScore { get; init; } = .5;
    public double CandlePinBarScore { get; init; } = .4;

    /// <summary>선행 하락 조건 — 패턴 봉의 저가가 직전 N봉 최저를 갱신해야 한다.</summary>
    public int CandlePriorLowLookbackBars { get; init; } = 5;

    /// <summary>몸통이 ATR의 이 비율 미만이면 confidence를 낮춘다(zone 근접을 알 수 없는 대체 기준).</summary>
    public double CandleBodyAtrRatio { get; init; } = .3;
    public double CandleWeakBodyConfidence { get; init; } = .5;

    /// <summary>
    /// C4 상관군. 같은 정보축 묶음은 군 안에서 1/n로 나눈다. `군이름:기법,기법` 형식이며 2·3군이
    /// 들어오면 이 목록만 늘어난다. 1군 채택 목록에는 각 군에 한 기법씩만 있어 실질 계수는 1.0이다.
    /// </summary>
    public ImmutableArray<string> CorrelationGroups { get; init; } = DefaultCorrelationGroups;

    static readonly ImmutableArray<string> DefaultCorrelationGroups =
    [
        "oscillator:RSI,STOCH,WILLIAMS_R",
        "volatilityBand:BB_PERCENT_B,KELTNER",
        "range:DONCHIAN,ORB15"
    ];

    public static readonly ConfluencePolicy Default = new();

    public TimeSpan OpeningRangeSpan() => TimeSpan.FromMinutes(OpeningRangeMinutes);

    public TimeSpan BenchmarkSyncTolerance() => TimeSpan.FromSeconds(BenchmarkSyncToleranceSeconds);

    /// <summary>기법 이름이 속한 상관군 이름. 어느 군에도 없으면 null이며 계수는 1.0이다.</summary>
    public string? CorrelationGroupOf(string techniqueName)
    {
        ArgumentNullException.ThrowIfNull(techniqueName);
        foreach (var entry in CorrelationGroups)
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0) continue;
            var members = entry[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                               StringSplitOptions.TrimEntries);
            if (members.Contains(techniqueName, StringComparer.Ordinal)) return entry[..separator];
        }
        return null;
    }

    /// <summary>canonical JSON: 정책 입력 속성만, 이름 ordinal 정렬, 고정 수치 표현. 실행 시각·경로는 넣지 않는다.</summary>
    public string CanonicalJson
    {
        get
        {
            var builder = new StringBuilder("{");
            var first = true;
            foreach (var property in PolicyInputs)
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append(Quote(property.Name)).Append(':').Append(Canonical(property.GetValue(this)));
            }
            return builder.Append('}').ToString();
        }
    }

    /// <summary>canonical JSON의 SHA-256 소문자 hex. StructurePolicy.PolicyHash와 독립이다.</summary>
    public string PolicyHash => StructureMath.Sha256Hex(CanonicalJson);

    static readonly PropertyInfo[] PolicyInputs = typeof(ConfluencePolicy)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(x => x.CanRead && x.SetMethod is { IsPublic: true })
        .OrderBy(x => x.Name, StringComparer.Ordinal)
        .ToArray();

    static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c < ' ' ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString()
            });
        return builder.Append('"').ToString();
    }

    static string Canonical(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => Quote(s),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal m => StructureMath.Price(m),
        IEnumerable items => "[" + string.Join(',', items.Cast<object?>().Select(Canonical)) + "]",
        double d => double.IsFinite(d)
            ? d.ToString("R", CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("Policy numbers must be finite; NaN/Infinity is never serialized."),
        _ => throw new NotSupportedException($"Unsupported policy value type {value.GetType().Name}.")
    };
}
