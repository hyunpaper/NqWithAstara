using System.Collections.Immutable;

namespace Astra.Server.Domain.Confluence;

/// <summary>비율 신뢰구간 (C5, #169).</summary>
public sealed record ProportionInterval(double Low, double High);

/// <summary>
/// K4 통계 정의 (C5, #169). Wilson score interval · Brier score · 이항 정확검정 · Benjamini-Hochberg.
/// 전부 순수 함수이며 수식은 참조 수치 테스트로 고정한다.
/// </summary>
public static class MeasurementStatistics
{
    /// <summary>표준정규 양측 95% 분위수.</summary>
    public const double NormalQuantile95 = 1.959963984540054;

    /// <summary>
    /// Wilson score interval:
    /// (p̂ + z²/2n ± z·√(p̂(1−p̂)/n + z²/4n²)) / (1 + z²/n).
    /// </summary>
    public static ProportionInterval Wilson(int successes, int trials, double z = NormalQuantile95)
    {
        if (trials <= 0) return new ProportionInterval(0, 1);
        if (successes < 0 || successes > trials) throw new ArgumentOutOfRangeException(nameof(successes));

        double n = trials, p = (double)successes / trials, zz = z * z;
        var denominator = 1 + zz / n;
        var center = (p + zz / (2 * n)) / denominator;
        var half = z / denominator * Math.Sqrt(p * (1 - p) / n + zz / (4 * n * n));
        return new ProportionInterval(Math.Max(0, center - half), Math.Min(1, center + half));
    }

    /// <summary>Brier score: (1/n)·Σ(f−o)². f는 예측확률, o는 결과 1/0이다.</summary>
    public static double Brier(IReadOnlyList<(double Forecast, bool Outcome)> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0) return double.NaN;
        double total = 0;
        foreach (var (forecast, outcome) in observations)
        {
            var error = forecast - (outcome ? 1 : 0);
            total += error * error;
        }
        return total / observations.Count;
    }

    /// <summary>기법 score를 예측확률로 옮긴다: (score+1)/2 (C5).</summary>
    public static double ForecastProbability(double score) =>
        !double.IsFinite(score) ? .5 : Math.Clamp((score + 1) / 2, 0, 1);

    /// <summary>
    /// 이항 정확검정 양측 p값. 귀무가설 p=0.5이며 대칭이므로 min(2·P(X≤k), 2·P(X≥k), 1)이다.
    /// </summary>
    public static double BinomialTwoSidedP(int successes, int trials, double probability = .5)
    {
        if (trials <= 0) return 1;
        if (successes < 0 || successes > trials) throw new ArgumentOutOfRangeException(nameof(successes));
        if (probability is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(probability));

        double lower = 0, upper = 0;
        for (var j = 0; j <= trials; j++)
        {
            var mass = Math.Exp(LogChoose(trials, j) + j * Math.Log(probability) +
                                (trials - j) * Math.Log(1 - probability));
            if (j <= successes) lower += mass;
            if (j >= successes) upper += mass;
        }
        return Math.Min(1, 2 * Math.Min(lower, upper));
    }

    /// <summary>
    /// Benjamini-Hochberg (FDR=q): p값을 오름차순으로 두고 p(k) ≤ (k/m)·q를 만족하는 최대 k까지 기각한다.
    /// 입력 순서 그대로 기각 여부를 돌려준다.
    /// </summary>
    public static ImmutableArray<bool> BenjaminiHochberg(IReadOnlyList<double> pValues, double q)
    {
        ArgumentNullException.ThrowIfNull(pValues);
        if (q is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(q));
        var m = pValues.Count;
        if (m == 0) return [];

        var order = Enumerable.Range(0, m).OrderBy(i => pValues[i]).ToArray();
        var cutoff = -1;
        for (var rank = 1; rank <= m; rank++)
            if (pValues[order[rank - 1]] <= (double)rank / m * q)
                cutoff = rank;

        var rejected = new bool[m];
        for (var rank = 1; rank <= cutoff; rank++) rejected[order[rank - 1]] = true;
        return [.. rejected];
    }

    static double LogChoose(int n, int k) => LogGamma(n + 1) - LogGamma(k + 1) - LogGamma(n - k + 1);

    static readonly double[] LanczosCoefficients =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
        1.5056327351493116e-7
    ];

    /// <summary>Lanczos 근사(g=7) 로그 감마. 이항 계수를 큰 n에서도 안정적으로 계산한다.</summary>
    static double LogGamma(double x)
    {
        if (x < .5) return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        x -= 1;
        var a = LanczosCoefficients[0];
        var t = x + 7.5;
        for (var i = 1; i < LanczosCoefficients.Length; i++) a += LanczosCoefficients[i] / (x + i);
        return .5 * Math.Log(2 * Math.PI) + (x + .5) * Math.Log(t) - t + Math.Log(a);
    }
}
