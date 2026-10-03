using System.Text.Json;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application;

public sealed record EntryObservationReasonCount(string Code, int Count);

public sealed record EntryObservationCandidateRow(string Symbol, string EventId, string Kind, string State,
    DateTimeOffset? TriggerBarStart, DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt,
    string[] RejectionCodes, string PolicyHash);

public sealed record EntryObservationHistoryWindow(string Label, DateOnly From, DateOnly To, int Files,
    int Observations, int Symbols, int Candidates, int Ready, int Entered, int Rejected,
    EntryObservationReasonCount[] TopReasons, int CorruptLines)
{
    public int NonEntrySideCandidates { get; init; }
    public EntryObservationReasonCount[] NonEntrySideTopReasons { get; init; } = [];
}

/// <summary>관측 jsonl 기반 누적(오늘·현재 정책). 재기동·화면 전환과 무관하게 같은 값을 돌려준다.</summary>
public sealed record EntryObservationHistoryReport(DateTimeOffset GeneratedAt, string PolicyHash,
    EntryObservationHistoryWindow Today, EntryObservationHistoryWindow CurrentPolicy,
    EntryObservationCandidateRow[] RecentCandidates, string? Warning);

/// <summary>
/// 관측 파일(structure-observations-&lt;날짜&gt;.jsonl)을 증분 집계한다. 과거 일자는 한 번만 읽고,
/// 오늘 파일은 크기가 바뀐 경우에만 새 줄을 읽는다. 읽기 실패는 마지막 집계를 유지하고 경고로 드러낸다.
/// </summary>
public sealed class EntryObservationHistoryQueryService(IStructureObservationStore store, TimeProvider clock,
    StructurePolicy? policy = null)
{
    public const int HistoryDays = 7;
    public const int TopReasonLimit = 8;
    public const int RecentCandidateLimit = 100;

    static readonly string[] RejectedStates = ["REJECTED", "INVALIDATED", "EXPIRED"];

    readonly StructurePolicy _policy = policy ?? StructurePolicy.Default;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, FileAggregate> _files = new(StringComparer.Ordinal);

    public string PolicyHash => _policy.PolicyHash;

    public async Task<EntryObservationHistoryReport> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = MarketRules.TradingDate(now);
        var from = today.AddDays(-(HistoryDays - 1));
        string? warning = null;

        await _gate.WaitAsync(ct);
        try
        {
            for (var day = from; day <= today; day = day.AddDays(1))
            {
                var file = StructureObservationWriter.FileName(day);
                try { await RefreshAsync(file, day, day == today, ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    warning = $"관측 파일 읽기 실패: {file} ({ex.GetType().Name})";
                }
            }

            foreach (var stale in _files.Keys.Where(x => _files[x].Day < from).ToArray()) _files.Remove(stale);

            var todayAggregate = _files.TryGetValue(StructureObservationWriter.FileName(today), out var t) ? [t] : Array.Empty<FileAggregate>();
            var all = _files.Values.Where(x => x.Day >= from).OrderBy(x => x.Day).ToArray();
            var todayWindow = Window("오늘", today, today, todayAggregate, null, _policy);
            var policyWindow = Window("현재 정책", from, today, all, PolicyHash, _policy);
            var recent = all.SelectMany(x => x.Candidates.Values)
                .OrderByDescending(x => x.LastObservedAt).ThenBy(x => x.Symbol, StringComparer.Ordinal)
                .Take(RecentCandidateLimit)
                .Select(x => new EntryObservationCandidateRow(x.Symbol, x.EventId, x.Kind, x.State, x.TriggerBarStart,
                    x.FirstObservedAt, x.LastObservedAt, x.RejectionCodes.Order(StringComparer.Ordinal).ToArray(), x.PolicyHash))
                .ToArray();
            return new(now, PolicyHash, todayWindow, policyWindow, recent, warning);
        }
        finally { _gate.Release(); }
    }

    async Task RefreshAsync(string file, DateOnly day, bool mutable, CancellationToken ct)
    {
        if (_files.TryGetValue(file, out var existing) && !mutable) return;
        var size = await store.SizeAsync(file, ct);
        if (existing is not null && existing.Bytes == size) return;
        if (existing is not null && size < existing.Bytes) existing = null;

        var aggregate = existing ?? new FileAggregate(day);
        var lines = await store.ReadLinesAsync(file, aggregate.Lines, ct);
        foreach (var line in lines) aggregate.Apply(line);
        aggregate.Lines += lines.Count;
        aggregate.Bytes = size;
        _files[file] = aggregate;
    }

    /// <summary>후보 수·사유 Top은 진입 가능한 방향 후보만 센다. 정책상 진입 불가 방향(숏)은 별도 필드로 둔다(§9.1).</summary>
    static EntryObservationHistoryWindow Window(string label, DateOnly from, DateOnly to,
        IReadOnlyCollection<FileAggregate> files, string? policyHash, StructurePolicy policy)
    {
        bool Match(string hash) => policyHash is null || string.Equals(hash, policyHash, StringComparison.Ordinal);
        var all = files.SelectMany(x => x.Candidates.Values).Where(x => Match(x.PolicyHash)).ToArray();
        var candidates = all.Where(x => SetupKinds.CanEnter(x.Kind, policy)).ToArray();
        var nonEntrySide = all.Where(x => !SetupKinds.CanEnter(x.Kind, policy)).ToArray();
        var reasons = CountCodes(candidates);
        foreach (var file in files)
            foreach (var blocker in file.Blockers.Where(x => Match(x.PolicyHash)))
                reasons[blocker.Code] = reasons.GetValueOrDefault(blocker.Code) + 1;
        var symbols = files.SelectMany(x => x.Symbols.Where(s => Match(s.PolicyHash)).Select(s => s.Symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new EntryObservationHistoryWindow(label, from, to, files.Count,
            files.Sum(x => policyHash is null ? x.Observations : x.ObservationsByPolicy.GetValueOrDefault(policyHash)),
            symbols, candidates.Length,
            candidates.Count(x => x.SeenReady), candidates.Count(x => x.SeenEntered),
            candidates.Count(x => RejectedStates.Contains(x.State, StringComparer.Ordinal)),
            Top(reasons), files.Sum(x => x.CorruptLines))
        {
            NonEntrySideCandidates = nonEntrySide.Length,
            NonEntrySideTopReasons = Top(CountCodes(nonEntrySide))
        };
    }

    static Dictionary<string, int> CountCodes(IEnumerable<CandidateAggregate> candidates)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
            foreach (var code in candidate.RejectionCodes) counts[code] = counts.GetValueOrDefault(code) + 1;
        return counts;
    }

    static EntryObservationReasonCount[] Top(Dictionary<string, int> reasons) =>
        reasons.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(TopReasonLimit).Select(x => new EntryObservationReasonCount(x.Key, x.Value)).ToArray();

    sealed class CandidateAggregate(string symbol, string eventId, string kind, string policyHash,
        DateTimeOffset? triggerBarStart, DateTimeOffset firstObservedAt)
    {
        public string Symbol { get; } = symbol;
        public string EventId { get; } = eventId;
        public string Kind { get; } = kind;
        public string PolicyHash { get; } = policyHash;
        public DateTimeOffset? TriggerBarStart { get; } = triggerBarStart;
        public DateTimeOffset FirstObservedAt { get; } = firstObservedAt;
        public DateTimeOffset LastObservedAt { get; set; } = firstObservedAt;
        public string State { get; set; } = "WAIT";
        public bool SeenReady { get; set; }
        public bool SeenEntered { get; set; }
        public HashSet<string> RejectionCodes { get; } = new(StringComparer.Ordinal);
    }

    sealed class FileAggregate(DateOnly day)
    {
        public DateOnly Day { get; } = day;
        public int Lines { get; set; }
        public long Bytes { get; set; }
        public int Observations { get; private set; }
        public int CorruptLines { get; private set; }
        public Dictionary<string, int> ObservationsByPolicy { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CandidateAggregate> Candidates { get; } = new(StringComparer.Ordinal);
        public HashSet<(string Symbol, string PolicyHash)> Symbols { get; } = [];
        public HashSet<(string Symbol, DateTimeOffset? Bar, string Code, string PolicyHash)> Blockers { get; } = [];

        public void Apply(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { CorruptLines++; return; }
                var symbol = Text(root, "symbol");
                if (symbol is null) { CorruptLines++; return; }
                var policyHash = Text(root, "policyHash") ?? string.Empty;
                var observedAt = Time(root, "observedAt") ?? DateTimeOffset.MinValue;
                var bar = Time(root, "lastCompletedBarStart");
                Observations++;
                ObservationsByPolicy[policyHash] = ObservationsByPolicy.GetValueOrDefault(policyHash) + 1;
                Symbols.Add((symbol, policyHash));

                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
                    foreach (var candidate in candidates.EnumerateArray()) ApplyCandidate(symbol, policyHash, observedAt, candidate);

                if (root.TryGetProperty("quality", out var quality) && quality.ValueKind == JsonValueKind.Object)
                {
                    foreach (var code in Strings(quality, "blockersForCandidate")) Blockers.Add((symbol, bar, code, policyHash));
                    foreach (var code in Strings(quality, "blockersForReady")) Blockers.Add((symbol, bar, code, policyHash));
                }
            }
            catch (JsonException) { CorruptLines++; }
        }

        void ApplyCandidate(string symbol, string policyHash, DateTimeOffset observedAt, JsonElement candidate)
        {
            if (candidate.ValueKind != JsonValueKind.Object) return;
            var eventId = Text(candidate, "eventId");
            if (eventId is null) return;
            if (!Candidates.TryGetValue(eventId, out var aggregate))
                Candidates[eventId] = aggregate = new CandidateAggregate(symbol, eventId, Text(candidate, "kind") ?? "?",
                    policyHash, Time(candidate, "triggerBarStart"), observedAt);
            if (observedAt >= aggregate.LastObservedAt)
            {
                aggregate.LastObservedAt = observedAt;
                aggregate.State = Text(candidate, "state") ?? aggregate.State;
            }
            var state = Text(candidate, "state");
            if (state == "READY") aggregate.SeenReady = true;
            if (state == "ENTERED") { aggregate.SeenReady = true; aggregate.SeenEntered = true; }
            foreach (var code in Strings(candidate, "rejectionCodes")) aggregate.RejectionCodes.Add(code);
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        static DateTimeOffset? Time(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.TryGetDateTimeOffset(out var time) ? time : null;

        static IEnumerable<string> Strings(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) yield return text;
        }
    }
}
