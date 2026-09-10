using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 설계 §16 저장량·성능 및 관측. 일자 기준 NY 거래일, 동일 관측 1회, 20 MiB 상한,
/// stable observation ID로 재시작 append 중복 방지, NaN/Infinity 미기록을 고정한다.
/// </summary>
public sealed class StructureD3ObservationTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static StructureObservationRecord Record(int barMinute = 30, string detail = "summary",
        DateTimeOffset? observedAt = null, StructureTrendDto? trend = null, int padding = 0)
    {
        var id = StructuralLifecycle.ObservationId(D3.Symbol, D3.At(barMinute), P.PolicyHash);
        return new StructureObservationRecord(id, P.Version + "-shadow", D3.Symbol,
            observedAt ?? D3.At(barMinute + 1), D3.SessionStart, D3.At(barMinute + 1), D3.At(barMinute + 1),
            D3.At(barMinute), P.PolicyHash, P.Version, "shadow", "v4", detail,
            StructureAnalysisStatus.Available, "WAIT", null, trend, null, null, [],
            Enumerable.Range(0, padding).Select(i => $"PAD_{i}").ToArray(), []);
    }

    static StructureObservationWriter Writer(MemoryObservationStore store) => new(store, P);

    /// <summary>§16: 일자 기준은 New York 거래일이다. UTC/KST 날짜로 나누지 않는다.</summary>
    [Fact]
    public async Task ObservationFileIsNamedByTheNewYorkTradingDayNotByUtcOrKst()
    {
        var store = new MemoryObservationStore();
        // ObservedAt은 KST로는 다음 날(05:30)이지만 NY 거래일은 그대로 2026-09-09다.
        var lateInSession = DateTimeOffset.Parse("2026-09-09T20:30:00+00:00");
        Assert.Equal(10, TimeZoneInfo.ConvertTimeBySystemTimeZoneId(lateInSession, "Asia/Seoul").Day);

        await Writer(store).AppendAsync(Record(observedAt: lateInSession), default);

        var file = Assert.Single(store.Files.Keys);
        Assert.Equal("structure-observations-2026-09-09.jsonl", file);
        Assert.Equal(StructureObservationWriter.FileName(MarketRules.TradingDate(D3.SessionStart)), file);
    }

    /// <summary>동일 symbol/lastCompletedBar/policyHash 관측은 한 번만 기록한다(§16).</summary>
    [Fact]
    public async Task SameSymbolBarAndPolicyIsRecordedOnlyOnce()
    {
        var store = new MemoryObservationStore();
        var writer = Writer(store);

        var first = await writer.AppendAsync(Record(), default);
        var second = await writer.AppendAsync(Record(), default);
        var nextBar = await writer.AppendAsync(Record(31), default);

        Assert.True(first.Written);
        Assert.False(second.Written);
        Assert.True(second.Duplicate);
        Assert.True(nextBar.Written);
        Assert.Equal(2, store.Appends);
        Assert.Equal(2, store.AllLines.Count());
    }

    /// <summary>재시작·재실행 시 append 중복은 stable observation ID로 막는다(§16).</summary>
    [Fact]
    public async Task RestartDoesNotAppendTheSameObservationTwice()
    {
        var store = new MemoryObservationStore();
        await Writer(store).AppendAsync(Record(), default);
        Assert.Single(store.AllLines);

        // 새 writer 인스턴스(=프로세스 재시작)가 기존 파일의 ID를 읽어 중복을 거절한다.
        var restarted = await Writer(store).AppendAsync(Record(), default);
        Assert.False(restarted.Written);
        Assert.True(restarted.Duplicate);
        Assert.Single(store.AllLines);
        Assert.Equal(1, store.Appends);
    }

    [Fact]
    public async Task CorruptedExistingLinesAreIgnoredWithoutDeletingAnything()
    {
        var store = new MemoryObservationStore();
        store.Files["structure-observations-2026-09-09.jsonl"] = ["{not json", string.Empty];

        var result = await Writer(store).AppendAsync(Record(), default);

        Assert.True(result.Written);
        Assert.Equal(3, store.Lines("structure-observations-2026-09-09.jsonl").Count);
        Assert.Contains("{not json", store.AllLines);
    }

    /// <summary>§16: 일자당 상한을 넘으면 더 저장하지 않고 경고를 노출하되 기존 기록을 삭제하지 않는다.</summary>
    [Fact]
    public async Task DailyByteLimitStopsWritingAndExposesObservationStorageLimited()
    {
        var store = new MemoryObservationStore { ForcedSize = P.ObservationDailyByteLimit - 10 };
        var writer = Writer(store);

        var blocked = await writer.AppendAsync(Record(), default);

        Assert.False(blocked.Written);
        Assert.True(blocked.Limited);
        Assert.True(writer.Limited);
        Assert.Equal(StructureObservationWriter.LimitWarning, "ObservationStorageLimited");
        Assert.Equal(0, store.Appends);
        Assert.Empty(store.AllLines);
    }

    [Fact]
    public async Task WritesAreAllowedUntilTheLimitIsActuallyReached()
    {
        var store = new MemoryObservationStore();
        var small = new StructureObservationWriter(store, P with { ObservationDailyByteLimit = 4096 });

        var written = 0;
        for (var i = 0; i < 40; i++)
            if ((await small.AppendAsync(Record(i, padding: 4), default)).Written) written++;

        Assert.InRange(written, 1, 39);
        Assert.True(small.Limited);
        Assert.Equal(written, store.AllLines.Count());
        Assert.True(store.AllLines.Sum(x => (long)System.Text.Encoding.UTF8.GetByteCount(x) + 1) <= 4096);
    }

    /// <summary>§11: NaN/Infinity를 JSON에 쓰지 않는다. 결측은 null이며 0으로 대체하지 않는다.</summary>
    [Fact]
    public async Task NonFiniteIndicatorValuesAreRecordedAsNullNeverAsNaN()
    {
        var poisoned = new TrendAssessment(TrendState.Up, double.NaN, double.PositiveInfinity,
            double.NegativeInfinity, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            false, 40, D3.At(40), ImmutableArray<TrendComponent>.Empty, ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);
        var dto = StructureViewMapper.Trend(poisoned);

        Assert.Null(dto.SignedTrend);
        Assert.Null(dto.PriceDirection);
        Assert.Null(dto.StructureDirection);
        Assert.Null(dto.Efficiency);
        Assert.Null(dto.Atr1m);
        Assert.Null(dto.Vwap);

        var store = new MemoryObservationStore();
        await Writer(store).AppendAsync(Record(trend: dto), default);

        var line = Assert.Single(store.AllLines);
        Assert.DoesNotContain("NaN", line);
        Assert.DoesNotContain("Infinity", line);
        using var document = JsonDocument.Parse(line);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("trend").GetProperty("signedTrend").ValueKind);
    }

    [Fact]
    public async Task EachRecordIsExactlyOneJsonLineCarryingTheDesignFields()
    {
        var store = new MemoryObservationStore();
        await Writer(store).AppendAsync(Record(), default);

        var line = Assert.Single(store.AllLines);
        Assert.DoesNotContain('\n', line);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        foreach (var field in new[]
                 {
                     "observationId", "recordVersion", "symbol", "observedAt", "sessionStart", "analysisAsOf",
                     "lastCompletedBarStart", "policyHash", "engineVersion", "mode", "entryOwner", "detail",
                     "status", "candidateSummary", "candidates", "warnings", "notes"
                 })
            Assert.True(root.TryGetProperty(field, out _), field);
        // §11/§16B: shadow 관측 레코드 버전은 v5-structure.1-shadow이며 SimTrade.Logic에 쓰지 않는다.
        Assert.Equal("v5-structure.1-shadow", root.GetProperty("recordVersion").GetString());
    }

    [Fact]
    public async Task LatchFileIsStoredInTheV5StoreNotInTheTradeStore()
    {
        var store = new MemoryObservationStore();
        var latch = StructuralLatch.Empty(D3.Symbol, D3.SessionStart, P.PolicyHash);
        await store.WriteTextAsync(StructureAnalysisService.LatchFile, StructureLatchStorage.Serialize([latch]), default);

        Assert.True(store.Texts.ContainsKey(StructureAnalysisService.LatchFile));
        Assert.Equal("structure-lifecycle.json", StructureAnalysisService.LatchFile);
        Assert.Single(StructureLatchStorage.Parse(store.Texts[StructureAnalysisService.LatchFile]));
        Assert.Empty(StructureLatchStorage.Parse(null));
        Assert.Empty(StructureLatchStorage.Parse("  "));
    }
}
