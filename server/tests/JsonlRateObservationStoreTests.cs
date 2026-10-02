using System.Text.Json;
using Astra.Server.Application.Rates;
using Astra.Server.Infrastructure.Rates;
using Xunit;

namespace Astra.Server.Tests;

public sealed class JsonlRateObservationStoreTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
    readonly string _root = Directory.CreateTempSubdirectory("astra-rates-store-").FullName;

    static RateObservation Row(DateTimeOffset at, string tenor, double value, string kind = RateObservationKinds.Intraday)
        => new(at, tenor, value, "yahoo:^TNX", at.AddSeconds(-5), value + 0.05, kind);

    [Fact]
    public async Task 수집_시각의_UTC_날짜_파일에_camelCase_한_줄씩_append한다()
    {
        var store = new JsonlRateObservationStore(_root);

        await store.AppendAsync([Row(Now, "10Y", 5.237), Row(Now, "30Y", 5.603)], CancellationToken.None);
        await store.AppendAsync([Row(Now.AddMinutes(1), "10Y", 5.240)], CancellationToken.None);

        var path = Path.Combine(_root, "2026-10-02.jsonl");
        var lines = (await File.ReadAllLinesAsync(path)).Where(x => x.Length > 0).ToArray();
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(["at", "tenor", "value", "source", "asOf", "previousClose", "kind"], first.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal("10Y", first.RootElement.GetProperty("tenor").GetString());
        Assert.Equal(5.237, first.RootElement.GetProperty("value").GetDouble());
        Assert.Equal("intraday", first.RootElement.GetProperty("kind").GetString());
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public async Task 당일_읽기는_깨진_줄을_건너뛰고_나머지를_돌려준다()
    {
        var store = new JsonlRateObservationStore(_root);
        await store.AppendAsync([Row(Now, "10Y", 5.237)], CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(_root, "2026-10-02.jsonl"), "{broken\n\n");
        await store.AppendAsync([Row(Now.AddMinutes(2), "30Y", 5.603)], CancellationToken.None);

        var rows = await store.ReadDayAsync(new DateOnly(2026, 10, 2), CancellationToken.None);

        Assert.Equal(["10Y", "30Y"], rows.Select(x => x.Tenor));
        Assert.Equal(5.603, rows[1].Value);
        Assert.Equal(Now.AddMinutes(2), rows[1].At);
        Assert.Empty(await store.ReadDayAsync(new DateOnly(2026, 10, 3), CancellationToken.None));
    }

    [Fact]
    public async Task 보관_기간을_지난_날짜_파일만_지운다()
    {
        var store = new JsonlRateObservationStore(_root);
        await store.AppendAsync([Row(Now.AddDays(-91), "10Y", 5.0)], CancellationToken.None);
        await store.AppendAsync([Row(Now.AddDays(-90), "10Y", 5.0)], CancellationToken.None);
        await store.AppendAsync([Row(Now, "10Y", 5.0)], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.jsonl"), "{}\n");

        var deleted = await store.PruneAsync(Now, 90, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(["2026-07-04.jsonl", "2026-10-02.jsonl", "notes.jsonl"],
            Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task 폴더가_없으면_정리는_0이고_읽기는_빈_목록이다()
    {
        var store = new JsonlRateObservationStore(Path.Combine(_root, "missing"));

        Assert.Equal(0, await store.PruneAsync(Now, 90, CancellationToken.None));
        Assert.Empty(await store.ReadDayAsync(new DateOnly(2026, 10, 2), CancellationToken.None));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
