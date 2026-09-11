using System.Text;
using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Astra.Server.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

/// <summary>
/// 이슈 #44 — 관측 일자 한도 도달 시 보존 우선순위. 설계 §16(저장량·성능 및 관측) 해석:
/// 상한 자체는 그대로 두고 "무엇을 먼저 버릴지"만 정한다. 주기 WAIT 요약이 먼저 희생되고,
/// 상태 전이·후보 결정 관측은 예비 예산에서 zones 배열 없이라도 끝까지 남으며, 그래도 못 남기면
/// 누락 장부에 건수·시각·사유가 남는다. 결정적 fixture와 임시 디렉터리만 쓴다.
/// </summary>
public sealed class StructureObservationRetentionTests : IDisposable
{
    static readonly StructurePolicy P = StructurePolicy.Default;
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    readonly string _contentRoot = Directory.CreateTempSubdirectory("astra-obs-retention-").FullName;
    string StructureRoot => Path.Combine(_contentRoot, "App_Data", "structure");

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* 임시 디렉터리 정리 실패는 무시 */ }
    }

    // ── 결정적 fixture ─────────────────────────────────────────────────────────

    static StructureZoneDto Zone(int i) => new($"zone-{i}", 1, 1, 99m, 100m, "SUPPORT", "SUPPORT",
        D3.At(0), D3.At(10), ["pivot", "profile"], 3, 2, .5, .4, .3, .2, .1, 0, 4, 3, 1, 0,
        [], true, [], [], false, false, [$"src-{i}-a", $"src-{i}-b"],
        [new StructureEvidenceGroupDto("PIVOT", D3.At(0), D3.At(10), [$"src-{i}-a", $"src-{i}-b"])],
        [new StructureRoleChangeDto(D3.At(10), "UNRESOLVED", "SUPPORT", "CONFIRMED")]);

    static StructurePlanDto Plan() => new("plan-1", "PULLBACK", 100m, 99m, 98.90m, 102m, "zone-1", 98.80m, 99.20m,
        "zone-9", 101.80m, 102.20m, .15m, "ATR", .01m, 2m, 1.10m, 1.80m, 1.1, .02m, .01m, .03m, false,
        P.EligibilityCostModelVersion, P.RealizedFillCostModelVersion, D3.At(10), D3.At(15),
        P.Version, P.PolicyHash, ["ELIGIBLE"], "지지 구간 되돌림");

    static StructureCandidateDto Candidate(string eventId, string state) => new(eventId, "PULLBACK", "zone-1",
        D3.At(9), D3.At(10), D3.At(10), D3.At(15), state, .62, 100m, 99m, 98.90m, 102m, 1.80m, Plan(),
        [new StructureQualityComponentDto("trend", .6, .6, true)], [], [], false, true);

    static StructureObservationRecord Record(int bar, string detail, string summary, int zoneCount = 0,
        string symbol = D3.Symbol)
    {
        var id = StructuralLifecycle.ObservationId(symbol, D3.At(bar), P.PolicyHash);
        return new StructureObservationRecord(id, P.Version, symbol, D3.At(bar + 1), D3.SessionStart,
            D3.At(bar + 1), D3.At(bar + 1), D3.At(bar), P.PolicyHash, P.Version, "active", "v5", detail,
            StructureAnalysisStatus.Available, summary, summary == "WAIT" ? null : $"evt-{bar}", null, null,
            zoneCount == 0 ? null : [.. Enumerable.Range(0, zoneCount).Select(Zone)],
            summary == "WAIT" ? [] : [Candidate($"evt-{bar}", summary)], [], []);
    }

    static StructureObservationRecord Routine(int bar) => Record(bar, "summary", "WAIT");

    /// <summary>실제 2026-09-10 파일과 같은 구성: 결정 관측은 전부 full 스냅샷이고 zones가 대부분의 바이트다.</summary>
    static StructureObservationRecord Core(int bar, string summary = "ENTERED", int zoneCount = 100) =>
        Record(bar, "full", summary, zoneCount);

    static long Size(StructureObservationRecord record) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(record, Web)) + 1;

    static IReadOnlyList<JsonElement> Parsed(MemoryObservationStore store) =>
        [.. store.AllLines.Select(x => JsonDocument.Parse(x).RootElement)];

    // ── 분류 계약 ──────────────────────────────────────────────────────────────

    /// <summary>주기 소음은 "요약 + WAIT"뿐이다. 결정·상태 전이는 어떤 형태든 핵심으로 분류된다.</summary>
    [Theory]
    [InlineData("summary", "WAIT", false)]
    [InlineData("full", "WAIT", true)]
    [InlineData("summary", "READY", true)]
    [InlineData("summary", "ENTERED", true)]
    [InlineData("summary", "REJECTED", true)]
    [InlineData("full", "ENTERED", true)]
    [InlineData("full", "REJECTED", true)]
    [InlineData("full", "INVALIDATED", true)]
    [InlineData("full", "EXPIRED", true)]
    public void OnlyRepeatedWaitSummariesCountAsRoutineNoise(string detail, string summary, bool core)
    {
        var record = Record(1, detail, summary);
        Assert.Equal(core, StructureObservationWriter.IsCore(record));
        Assert.Equal(!core, StructureObservationWriter.IsRoutine(record));
    }

    [Fact]
    public void CoreReserveIsAShareOfTheDailyLimitNotAnIncreaseOfIt()
    {
        var writer = new StructureObservationWriter(new MemoryObservationStore(), P);
        Assert.Equal(.20, P.ObservationCoreReserveRatio);
        Assert.Equal(20L * 1024 * 1024, P.ObservationDailyByteLimit);
        Assert.Equal(4L * 1024 * 1024, writer.CoreReserveBytes);
        // 예비 구간은 상한 "안"이다 — 일일 디스크 상한은 20 MiB 그대로다.
        Assert.True(writer.CoreReserveBytes < P.ObservationDailyByteLimit);
        // 정책 수치이므로 바뀌면 PolicyHash가 바뀐다(§16A).
        Assert.NotEqual(P.PolicyHash, (P with { ObservationCoreReserveRatio = .25 }).PolicyHash);
    }

    // ── 한도 근접: 요약을 먼저 버리고 핵심은 남긴다 ──────────────────────────────

    [Fact]
    public async Task RoutineSummariesAreSacrificedFirstAndTheEntryObservationSurvives()
    {
        // 일반 예산은 정확히 소진, 예비 예산은 그대로인 상태에서 시작한다.
        var policy = P with { ObservationDailyByteLimit = 200_000 };
        var store = new MemoryObservationStore { ForcedSize = 160_000 };
        var writer = new StructureObservationWriter(store, policy);

        var routine = await writer.AppendAsync(Routine(30), default);
        var entry = await writer.AppendAsync(Core(31), default);

        Assert.False(routine.Written);
        Assert.True(routine.Limited);
        Assert.Equal(1, routine.RoutineDropCount);

        Assert.True(entry.Written);
        Assert.True(entry.Core);
        Assert.True(entry.ZonesOmitted);
        Assert.False(entry.CoreLimited);
        Assert.Equal(0, entry.CoreDropCount);

        // 진입 근거는 남고 반복 소음만 사라졌다.
        var kept = Assert.Single(Parsed(store));
        Assert.Equal("ENTERED", kept.GetProperty("candidateSummary").GetString());
        Assert.Equal("evt-31", kept.GetProperty("preferredCandidateId").GetString());
    }

    /// <summary>
    /// 축약 저장은 "필드·의미"를 바꾸지 않는다. nullable인 zones만 비우고(§16 "매 15초 전체 Zone 배열을
    /// 덤프하지 않는다") 이벤트 ID·시각·버전/policyHash·결정·품질·계획·호가 품질은 전부 남는다.
    /// </summary>
    [Fact]
    public async Task EssentialFormKeepsTheWholeDecisionContractAndOnlyDropsTheZoneArray()
    {
        var policy = P with { ObservationDailyByteLimit = 200_000 };
        var store = new MemoryObservationStore { ForcedSize = 160_000 };

        var result = await new StructureObservationWriter(store, policy).AppendAsync(Core(31), default);

        Assert.True(result is { Written: true, ZonesOmitted: true });
        var kept = Assert.Single(Parsed(store));
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("zones").ValueKind);
        // 이슈 완료 조건 2의 계약 — 전부 그대로다.
        Assert.Equal(StructuralLifecycle.ObservationId(D3.Symbol, D3.At(31), P.PolicyHash),
            kept.GetProperty("observationId").GetString());
        Assert.Equal(D3.Symbol, kept.GetProperty("symbol").GetString());
        Assert.Equal(P.PolicyHash, kept.GetProperty("policyHash").GetString());
        Assert.Equal(P.Version, kept.GetProperty("engineVersion").GetString());
        Assert.True(kept.TryGetProperty("observedAt", out _));
        Assert.True(kept.TryGetProperty("analysisAsOf", out _));
        Assert.True(kept.TryGetProperty("sessionStart", out _));

        var candidate = Assert.Single(kept.GetProperty("candidates").EnumerateArray());
        Assert.Equal("ENTERED", candidate.GetProperty("state").GetString());
        Assert.True(candidate.TryGetProperty("components", out _));
        Assert.True(candidate.TryGetProperty("rejectionCodes", out _));
        var plan = candidate.GetProperty("plan");
        // 선택 Zone ID와 당시 bounds는 계획 안에 남으므로 zones 생략으로도 근거 재구성이 가능하다(§11).
        Assert.Equal("zone-1", plan.GetProperty("invalidationZoneId").GetString());
        Assert.Equal(98.80m, plan.GetProperty("invalidationLower").GetDecimal());
        Assert.Equal("zone-9", plan.GetProperty("targetZoneId").GetString());
        Assert.Equal(102.20m, plan.GetProperty("targetUpper").GetDecimal());
        Assert.Equal(.03m, plan.GetProperty("validSpread").GetDecimal());
        // 축약 사실 자체가 레코드에 남는다 — 조용히 잘라내지 않는다.
        Assert.Contains(StructureObservationWriter.LimitWarning,
            kept.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task EssentialFormIsDramaticallySmallerThanTheFullSnapshot()
    {
        var record = Core(31);
        var policy = P with { ObservationDailyByteLimit = 200_000 };
        var store = new MemoryObservationStore { ForcedSize = 160_000 };

        await new StructureObservationWriter(store, policy).AppendAsync(record, default);

        var stored = Encoding.UTF8.GetByteCount(Assert.Single(store.AllLines)) + 1L;
        Assert.True(stored * 4 < Size(record), $"essential={stored} full={Size(record)}");
    }

    // ── 한도 초과 사실의 가시화 ────────────────────────────────────────────────

    [Fact]
    public async Task DroppedObservationsLeaveALedgerWithCountsTimesAndCause()
    {
        var policy = P with { ObservationDailyByteLimit = 200_000 };
        var store = new MemoryObservationStore { ForcedSize = 160_000 };
        var writer = new StructureObservationWriter(store, policy);

        await writer.AppendAsync(Routine(30), default);
        var second = await writer.AppendAsync(Routine(40), default);

        Assert.Equal(2, second.RoutineDropCount);
        var file = StructureObservationWriter.DropLedgerFileName(MarketRules.TradingDate(D3.SessionStart));
        var ledger = JsonSerializer.Deserialize<ObservationDropLedger>(store.Texts[file], Web)!;
        Assert.Equal(StructureObservationWriter.FileName(MarketRules.TradingDate(D3.SessionStart)), ledger.File);
        Assert.Equal(2, ledger.RoutineDropCount);
        Assert.Equal(0, ledger.CoreDropCount);
        Assert.Equal(D3.At(31), ledger.FirstDroppedAt);
        Assert.Equal(D3.At(41), ledger.LastDroppedAt);
        Assert.Equal([StructureObservationWriter.RoutineDropReason], ledger.Reasons);
        Assert.Equal(200_000, ledger.LimitBytes);
        Assert.Equal(40_000, ledger.CoreReserveBytes);
    }

    /// <summary>핵심 예비 예산까지 소진되면 "0건"이 아니라 "몇 건이 왜 없는지"가 남는다(이슈 완료 조건 3).</summary>
    [Fact]
    public async Task ExhaustingEvenTheCoreReserveIsLoudNotSilent()
    {
        var policy = P with { ObservationDailyByteLimit = 200_000 };
        var store = new MemoryObservationStore { ForcedSize = 199_990 };
        var writer = new StructureObservationWriter(store, policy);

        var dropped = await writer.AppendAsync(Core(31), default);

        Assert.False(dropped.Written);
        Assert.True(dropped.Limited);
        Assert.True(dropped.CoreLimited);
        Assert.True(writer.CoreLimited);
        Assert.Equal(1, dropped.CoreDropCount);
        Assert.Empty(store.AllLines);

        Assert.Equal(new[] { StructureObservationWriter.LimitWarning, StructureObservationWriter.CoreLimitWarning },
            StructureObservationWriter.StorageWarnings(dropped).ToArray());
        Assert.Equal("ObservationCoreStorageLimited", StructureObservationWriter.CoreLimitWarning);

        var ledger = writer.Drops!;
        Assert.Equal(1, ledger.CoreDropCount);
        Assert.Contains(StructureObservationWriter.CoreDropReason, ledger.Reasons);
        Assert.Equal(StructuralLifecycle.ObservationId(D3.Symbol, D3.At(31), P.PolicyHash),
            ledger.FirstDroppedObservationId);
    }

    [Fact]
    public void StorageWarningsStaySilentWhileNothingWasLost()
    {
        Assert.Empty(StructureObservationWriter.StorageWarnings(new ObservationWriteResult(true, false, false, 10)));
        Assert.Equal(new[] { StructureObservationWriter.LimitWarning },
            StructureObservationWriter.StorageWarnings(new ObservationWriteResult(false, false, true, 10)).ToArray());
    }

    /// <summary>재시작해도 누락 건수를 0으로 되돌리지 않는다 — 없는 이벤트를 0건으로 보고하지 않는다.</summary>
    [Fact]
    public async Task RestartRestoresTheDropLedgerInsteadOfReportingZero()
    {
        var policy = P with { ObservationDailyByteLimit = 200_000 };
        var store = new MemoryObservationStore { ForcedSize = 160_000 };
        await new StructureObservationWriter(store, policy).AppendAsync(Routine(30), default);

        // 새 writer 인스턴스 = 프로세스 재시작.
        var restarted = new StructureObservationWriter(store, policy);
        var next = await restarted.AppendAsync(Routine(40), default);

        Assert.True(restarted.Limited);
        Assert.Equal(2, next.RoutineDropCount);
        Assert.Equal(2, restarted.Drops!.RoutineDropCount);
        Assert.Equal(D3.At(31), restarted.Drops!.FirstDroppedAt);
    }

    [Fact]
    public async Task ACorruptedLedgerIsIgnoredWithoutInventingDrops()
    {
        var policy = P with { ObservationDailyByteLimit = 200_000 };
        var store = new MemoryObservationStore();
        store.Texts[StructureObservationWriter.DropLedgerFileName(MarketRules.TradingDate(D3.SessionStart))] =
            "{not json";
        var writer = new StructureObservationWriter(store, policy);

        var written = await writer.AppendAsync(Routine(30), default);

        Assert.True(written.Written);
        Assert.False(writer.Limited);
        Assert.Null(writer.Drops);
    }

    /// <summary>장부 쓰기 실패는 삼키지 않는다. 디스크에 못 남긴 누락을 메모리에서만 올려 두지도 않는다.</summary>
    [Fact]
    public async Task ALedgerWriteFailureSurfacesAndDoesNotAdvanceTheInMemoryCount()
    {
        var policy = P with { ObservationDailyByteLimit = 200_000 };
        var store = new MemoryObservationStore
        {
            ForcedSize = 160_000,
            TextWriteFailure = _ => new IOException("ledger write failed")
        };
        var writer = new StructureObservationWriter(store, policy);

        await Assert.ThrowsAsync<IOException>(() => writer.AppendAsync(Routine(30), default));

        Assert.Null(writer.Drops);
        Assert.Empty(store.AllLines);
    }

    /// <summary>동시 종목이 같은 일자 예산을 나눠 쓴다. 직렬화된 회계라 상한을 넘지 않고 핵심도 잃지 않는다.</summary>
    [Fact]
    public async Task ConcurrentSymbolsShareOneDailyBudgetWithoutLosingCoreObservations()
    {
        var policy = P with { ObservationDailyByteLimit = 400_000 };
        var store = new MemoryObservationStore();
        var writer = new StructureObservationWriter(store, policy);

        var symbols = new[] { "AAA", "BBB", "CCC", "DDD" };
        var records = Enumerable.Range(0, 40)
            .Select(i => i % 4 == 0
                ? Record(i, "full", "ENTERED", 40, symbols[i % symbols.Length])
                : Record(i, "summary", "WAIT", 0, symbols[i % symbols.Length]))
            .ToArray();

        var results = await Task.WhenAll(records.Select(x => writer.AppendAsync(x, default)));

        var bytes = store.AllLines.Sum(x => (long)Encoding.UTF8.GetByteCount(x) + 1);
        Assert.True(bytes <= policy.ObservationDailyByteLimit, $"{bytes}");
        Assert.Equal(store.AllLines.Count(), results.Count(x => x.Written));
        Assert.Equal(10, Parsed(store).Count(x => x.GetProperty("candidateSummary").GetString() == "ENTERED"));
        Assert.Equal(0, results.Sum(x => x.CoreDropCount));
        Assert.Equal(store.AllLines.Count(), store.AllLines.Distinct().Count());
    }

    // ── 기존 소비자 호환 ───────────────────────────────────────────────────────

    /// <summary>여유 예산에서는 저장 내용이 이전과 완전히 동일하다 — #27/#28 소비자가 보는 것이 바뀌지 않는다.</summary>
    [Fact]
    public async Task BelowTheReserveNothingAboutTheStoredRecordChanges()
    {
        var store = new MemoryObservationStore();
        var record = Core(31);

        var result = await new StructureObservationWriter(store, P).AppendAsync(record, default);

        Assert.True(result is { Written: true, Core: true, ZonesOmitted: false, Limited: false });
        Assert.Equal(JsonSerializer.Serialize(record, Web), Assert.Single(store.AllLines));
        Assert.Equal(100, Parsed(store)[0].GetProperty("zones").GetArrayLength());
        Assert.Empty(Parsed(store)[0].GetProperty("warnings").EnumerateArray());
        Assert.Equal(0, store.TextWrites);   // 누락이 없으면 장부 파일 자체가 생기지 않는다
    }

    /// <summary>장부는 관측 jsonl과 다른 파일이라 기존 glob·파서가 그대로 동작한다.</summary>
    [Fact]
    public void TheDropLedgerIsASeparateFileFromTheObservationJsonl()
    {
        var date = MarketRules.TradingDate(D3.SessionStart);
        var observations = StructureObservationWriter.FileName(date);
        var ledger = StructureObservationWriter.DropLedgerFileName(date);

        Assert.Equal("structure-observations-2026-09-09.jsonl", observations);
        Assert.Equal("structure-observation-drops-2026-09-09.json", ledger);
        Assert.NotEqual(observations, ledger);
        Assert.EndsWith(".jsonl", observations, StringComparison.Ordinal);
        Assert.False(ledger.StartsWith("structure-observations-", StringComparison.Ordinal));
    }

    // ── 실제 결함 재현 회귀 (2026-09-10) ───────────────────────────────────────

    /// <summary>
    /// 실측 구성(full 578행 평균 32.8 KB · summary 1636행 평균 1.2 KB, 합계 20,970,950 B)을 그대로 재현한다.
    /// 수정 전에는 02:35 이후 모든 신규 관측이 거절돼 진입 8건의 근거가 사라졌다. 수정 후에는
    /// 핵심 관측이 단 한 건도 버려지지 않고, 상한 20 MiB는 그대로 지켜진다.
    /// </summary>
    [Fact]
    public async Task TheFullDaySessionKeepsEveryCoreObservationWithinTheSameDailyLimit()
    {
        var store = new MemoryObservationStore();
        var writer = new StructureObservationWriter(store, P);

        var coreWritten = 0;
        var coreDropped = 0;
        var routineDropped = 0;
        var routineTotal = 0;
        var produced = 0L;
        // 수정 전 규칙(종류 무관 선착순) 아래에서 같은 입력이 어떻게 잘렸을지 함께 계산한다.
        var legacyBytes = 0L;
        var legacyCoreDropped = 0;

        var bar = 0;
        for (var block = 0; block < 578; block++)
        {
            var record = Core(bar++, "REJECTED");
            produced += Size(record);
            if (legacyBytes + Size(record) > P.ObservationDailyByteLimit) legacyCoreDropped++;
            else legacyBytes += Size(record);

            if ((await writer.AppendAsync(record, default)).Written) coreWritten++; else coreDropped++;

            // 실제 파일과 같은 578 : 1636 비율로 주기 요약을 섞는다.
            var routines = (block + 1) * 1636 / 578 - block * 1636 / 578;
            for (var i = 0; i < routines; i++)
            {
                var noise = Routine(bar++);
                routineTotal++;
                produced += Size(noise);
                if (legacyBytes + Size(noise) <= P.ObservationDailyByteLimit) legacyBytes += Size(noise);

                if (!(await writer.AppendAsync(noise, default)).Written) routineDropped++;
            }
        }

        Assert.Equal(1636, routineTotal);
        // 하루치 산출이 상한을 넘는 상황이어야 회귀로서 의미가 있다(실측 20,970,950 B와 같은 압박).
        Assert.True(produced > P.ObservationDailyByteLimit, $"produced={produced}");
        // 수정 전 규칙이었다면 핵심 관측이 잘렸다 — 이것이 이슈 #44의 결함이다.
        Assert.True(legacyCoreDropped > 0, "수정 전 규칙에서는 핵심 관측이 잘려야 한다");

        // 수정 후: 핵심 관측은 한 건도 잃지 않는다.
        Assert.Equal(578, coreWritten);
        Assert.Equal(0, coreDropped);
        Assert.False(writer.CoreLimited);
        Assert.True(routineDropped > 0, "주기 요약이 먼저 희생돼야 한다");
        Assert.True(writer.Limited);
        // 상한은 유지된다 — 무엇을 먼저 버릴지만 바뀌었다.
        var bytes = store.AllLines.Sum(x => (long)Encoding.UTF8.GetByteCount(x) + 1);
        Assert.True(bytes <= P.ObservationDailyByteLimit, $"{bytes} > {P.ObservationDailyByteLimit}");
        // 진입 근거는 전부 조회 가능하다.
        Assert.Equal(578, Parsed(store).Count(x => x.GetProperty("candidateSummary").GetString() == "REJECTED"));
    }

    // ── 저장 불변 조건 회귀 (실제 파일 어댑터) ─────────────────────────────────

    /// <summary>
    /// 실제 파일 어댑터 위에서 LF 고정·원자적 교체(.tmp + File.Move)·재시작 복원이 새 경로에서도 유지되는지.
    /// </summary>
    [Fact]
    public async Task FileStoreKeepsLfAtomicWritesAndRestartRestoreUnderPressure()
    {
        // 예비 비율 1.0 = 일반 예산 0 → 모든 주기 요약이 즉시 희생되고 핵심만 남는 극단 상황.
        var policy = P with { ObservationDailyByteLimit = 1_000_000, ObservationCoreReserveRatio = 1.0 };
        var store = new StructureObservationStore(new RetentionEnv(_contentRoot));
        var writer = new StructureObservationWriter(store, policy);

        var dropped = await writer.AppendAsync(Routine(30), default);
        var kept = await writer.AppendAsync(Record(31, "full", "ENTERED"), default);

        Assert.False(dropped.Written);
        Assert.True(kept.Written);

        var date = MarketRules.TradingDate(D3.SessionStart);
        var observations = Path.Combine(StructureRoot, StructureObservationWriter.FileName(date));
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(observations));
        Assert.DoesNotContain("\r", text);                                   // LF 고정
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Single(text.TrimEnd('\n').Split('\n'));
        Assert.Empty(Directory.GetFiles(StructureRoot, "*.tmp"));            // 원자적 교체 잔여물 없음

        var ledgerPath = Path.Combine(StructureRoot, StructureObservationWriter.DropLedgerFileName(date));
        var ledger = JsonSerializer.Deserialize<ObservationDropLedger>(
            await File.ReadAllTextAsync(ledgerPath), Web)!;
        Assert.Equal(1, ledger.RoutineDropCount);

        // 재시작: 새 store + 새 writer가 기존 ID·바이트·누락 장부를 모두 복원한다.
        var restarted = new StructureObservationWriter(
            new StructureObservationStore(new RetentionEnv(_contentRoot)), policy);
        var duplicate = await restarted.AppendAsync(Record(31, "full", "ENTERED"), default);

        Assert.True(duplicate.Duplicate);
        Assert.False(duplicate.Written);
        Assert.Equal(1, restarted.Drops!.RoutineDropCount);
        Assert.Single((await File.ReadAllLinesAsync(observations)));
    }

    /// <summary>ContentRootPath만 의미 있는 최소 가짜 환경. 실제 호스트를 띄우지 않는다.</summary>
    sealed class RetentionEnv(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Astra.Server.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }
}
