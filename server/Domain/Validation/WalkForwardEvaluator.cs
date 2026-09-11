namespace Astra.Server.Domain.Validation;

// 이슈 #28 — 시간 순서 학습/검증 분리(walk-forward)의 Domain 절반.
//
// 규칙:
//  1. 분할 단위는 New York 거래일이다. 한 세션이 학습과 검증에 동시에 들어가지 않는다.
//  2. 검증 구간의 모든 거래일은 학습 구간의 모든 거래일보다 뒤다(미래 데이터 누출 방지).
//  3. 세션 수가 모자라면 fold를 만들지 않고 "검증 불가"로 보고한다 — 표본을 늘리려고 무작위 분할하지 않는다.

public sealed record WalkForwardFold(int Index, DateOnly InSampleFrom, DateOnly InSampleTo, int InSampleSessions,
    DateOnly OutOfSampleFrom, DateOnly OutOfSampleTo, int OutOfSampleSessions, CohortEvaluation InSample,
    CohortEvaluation OutOfSample);

public sealed record WalkForwardReport(string Method, int Sessions, int RequestedFolds, int Folds,
    IReadOnlyList<WalkForwardFold> Result, IReadOnlyList<string> Limitations);

public static class WalkForwardEvaluator
{
    public const string Method = "anchored-walk-forward-by-ny-trading-date";
    public const string InsufficientSessions = "INSUFFICIENT_SESSIONS_FOR_WALK_FORWARD";

    /// <summary>기본 fold 수. 데이터에 맞춰 늘리거나 줄이지 않는다(임계값 탐색 금지).</summary>
    public const int DefaultFolds = 3;

    public static WalkForwardReport Evaluate(IReadOnlyList<LinkedCandidate> candidates, int folds = DefaultFolds,
        EvaluationThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (folds < 1) throw new ArgumentOutOfRangeException(nameof(folds));
        var limits = thresholds ?? EvaluationThresholds.Default;
        var rows = candidates.Where(x => x is not null).ToArray();
        var sessions = rows.Select(x => x.TradingDate).Distinct().Order().ToArray();

        // 검증 구간을 folds개 만들려면 앞에 학습 구간이 최소 하나 더 있어야 한다.
        if (sessions.Length < folds + 1)
            return new WalkForwardReport(Method, sessions.Length, folds, 0, [],
                [$"{InsufficientSessions}:{sessions.Length}/{folds + 1}"]);

        var blocks = Split(sessions, folds + 1);
        var result = new List<WalkForwardFold>(folds);
        for (var i = 0; i < folds; i++)
        {
            var inSample = blocks.Take(i + 1).SelectMany(x => x).ToArray();
            var outOfSample = blocks[i + 1];
            var inDates = inSample.ToHashSet();
            var outDates = outOfSample.ToHashSet();
            var inRows = rows.Where(x => inDates.Contains(x.TradingDate)).ToArray();
            var outRows = rows.Where(x => outDates.Contains(x.TradingDate)).ToArray();
            result.Add(new WalkForwardFold(i + 1, inSample[0], inSample[^1], inSample.Length,
                outOfSample[0], outOfSample[^1], outOfSample.Length,
                Evaluation(inRows, $"IN_SAMPLE_{i + 1}", "학습 구간", limits),
                Evaluation(outRows, $"OUT_OF_SAMPLE_{i + 1}", "검증 구간 (out-of-sample)", limits)));
        }

        var limitations = new List<string>();
        if (result.Any(x => x.OutOfSample.Verdict != EvaluationVerdict.Observed))
            limitations.Add("OUT_OF_SAMPLE_BELOW_THRESHOLD");
        return new WalkForwardReport(Method, sessions.Length, folds, result.Count, result, limitations);
    }

    static CohortEvaluation Evaluation(IReadOnlyList<LinkedCandidate> rows, string key, string label,
        EvaluationThresholds limits)
    {
        // 한 구간의 평가는 전체 평가와 같은 함수·같은 임계값을 쓴다(구간마다 다른 기준을 쓰지 않는다).
        var evaluation = ValidationEvaluator.Evaluate(rows, DateTimeOffset.MinValue, limits);
        return evaluation.Overall with { Key = key, Label = label };
    }

    /// <summary>
    /// 정렬된 거래일을 count개의 연속 블록으로 나눈다. 앞쪽 블록이 나머지를 한 개씩 더 가져가며
    /// 같은 거래일이 두 블록에 들어가지 않는다 — 이것이 누출 방지의 구조적 근거다.
    /// </summary>
    static DateOnly[][] Split(DateOnly[] sessions, int count)
    {
        var blocks = new DateOnly[count][];
        var size = sessions.Length / count;
        var remainder = sessions.Length % count;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            var length = size + (i < remainder ? 1 : 0);
            blocks[i] = sessions.Skip(offset).Take(length).ToArray();
            offset += length;
        }
        return blocks;
    }
}
