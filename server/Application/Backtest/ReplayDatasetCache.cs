using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Server.Domain;

namespace Astra.Server.Application.Backtest;

public sealed record ReplayDatasetSymbolProgress(string Symbol, int Pages, int RawBars, string? NextCursor,
    bool Completed, bool ReachedRequestedStart, DateTimeOffset? OldestBar, string? StopReason);

public sealed record ReplayDatasetManifest(string SchemaVersion, string DatasetId, string RequestKey,
    string Provider, bool Adjusted, string Timeframe, DateOnly From, DateOnly To, string Benchmark,
    ImmutableArray<string> Symbols, DateTimeOffset FetchStartedAt, DateTimeOffset? FetchedAt, string Status,
    string? FailureReason, ImmutableArray<ReplayDatasetSymbolProgress> Progress,
    ImmutableArray<ReplayImportSourceRow> Sources);

public sealed record ReplayDatasetLease(string DatasetId, string Root, ReplayDatasetManifest Manifest,
    ReplayImportReport Import, bool Reused);

/// <summary>
/// 외부 과거봉을 요청 계약별로 한 번 수집하고, 이후 replay가 읽는 불변 dataset으로 봉인한다.
/// 진행 중 페이지는 request lock 아래 원자 파일로 저장하므로 취소·재시작 뒤 이어받을 수 있다.
/// exact request key의 available/partial/no-data dataset은 기본 재사용한다. partial은 성과 계산을 허용하지
/// 않으며, sealed dataset이 없을 때만 기본 요청이 initial acquiring을 재개한다. sealed dataset과 failed refresh가
/// 함께 있으면 기본 요청은 sealed dataset을 사용하고 refresh 요청만 해당 acquisition을 재개한다.
/// </summary>
public sealed class ReplayDatasetCache(string appDataRoot, IHistoricalBarSource source, TimeProvider clock)
{
    public const string SchemaVersion = "replay-dataset.v1";
    public const string Timeframe = "1m";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    static readonly ConcurrentDictionary<string, DatasetFingerprint> ValidatedDatasets =
        new(StringComparer.OrdinalIgnoreCase);
    readonly string _root = Path.Combine(Path.GetFullPath(appDataRoot), "replay-datasets");

    public async Task<ReplayDatasetLease> AcquireAsync(IReadOnlyList<string> watchlist, string benchmark,
        DateOnly from, DateOnly to, bool refresh, CancellationToken ct)
    {
        var symbols = NormalizeSymbols(watchlist, benchmark);
        var normalizedBenchmark = benchmark.Trim().ToUpperInvariant();
        var normalizedWatchlist = symbols.Where(x => x != normalizedBenchmark).ToImmutableArray();
        var requestKey = RequestKey(source.Name, source.Adjusted, from, to, normalizedBenchmark, symbols);
        var contract = new DatasetContract(requestKey, source.Name, source.Adjusted, from, to,
            normalizedBenchmark, symbols);
        Directory.CreateDirectory(Path.Combine(_root, "locks"));
        await using var requestLock = await AcquireFileLockAsync(Path.Combine(_root, "locks", requestKey + ".lock"), ct);

        var indexPath = Path.Combine(_root, "index", requestKey + ".json");
        var index = await ReadAsync<DatasetIndex>(indexPath, ct) ?? new DatasetIndex(requestKey, null, null);
        if (!string.Equals(index.RequestKey, requestKey, StringComparison.Ordinal))
            index = new DatasetIndex(requestKey, null, null);
        var acquisitionId = ValidAcquisitionId(index.AcquisitionId, requestKey) ? index.AcquisitionId : null;
        var acquisitionExists = acquisitionId is not null && Directory.Exists(AcquisitionRoot(acquisitionId));
        var indexedDatasetExists = ValidDatasetId(index.DatasetId) && Directory.Exists(DatasetRoot(index.DatasetId!));
        var sealedDataset = await TryOpenDatasetAsync(index.DatasetId, contract, ct) ??
                            await FindDatasetAsync(contract, ct);
        if (sealedDataset is null && indexedDatasetExists)
            throw new InvalidDataException("replay dataset 무결성 또는 저장 계약 검증에 실패했습니다.");
        if (!refresh && sealedDataset is not null)
        {
            if (!string.Equals(index.DatasetId, sealedDataset.DatasetId, StringComparison.Ordinal) ||
                (!acquisitionExists && acquisitionId is not null))
                await WriteAtomicAsync(indexPath, new DatasetIndex(requestKey, sealedDataset.DatasetId,
                    acquisitionExists ? acquisitionId : null), ct);
            return sealedDataset with { Reused = true };
        }
        acquisitionId = acquisitionExists ? acquisitionId : $"{requestKey}-{Guid.NewGuid():N}";
        var acquisitionRoot = AcquisitionRoot(acquisitionId!);
        Directory.CreateDirectory(acquisitionRoot);
        index = new DatasetIndex(requestKey, sealedDataset?.DatasetId, acquisitionId);
        await WriteAtomicAsync(indexPath, index, ct);
        var startedAt = (await ReadAsync<ReplayDatasetManifest>(Path.Combine(acquisitionRoot, "manifest.json"), ct))
            ?.FetchStartedAt ?? clock.GetUtcNow();

        try
        {
            foreach (var symbol in symbols)
            {
                ct.ThrowIfCancellationRequested();
                var pages = await ReadPagesAsync(acquisitionRoot, symbol, ct);
                if (pages.Any(x => !string.Equals(x.BarTimeConvention, source.BarTimeConvention, StringComparison.Ordinal)))
                {
                    Directory.Delete(Path.Combine(acquisitionRoot, "raw", SafeSymbol(symbol)), true);
                    pages = [];
                }
                if (pages.LastOrDefault() is { } last && Terminal(last)) continue;
                var nextCursor = pages.LastOrDefault()?.NextCursor;
                var completedPages = pages.Count;
                var visitedCursors = pages.Select(x => x.RequestCursor).ToHashSet(StringComparer.Ordinal);
                if (nextCursor is not null && visitedCursors.Contains(nextCursor) && pages.Count > 0)
                {
                    var cycle = pages[^1] with
                    {
                        NextCursor = null,
                        StopReason = "Toss가 저장된 이전 페이지 커서를 순환해서 반환했습니다."
                    };
                    await WritePageAsync(acquisitionRoot, symbol, pages.Count - 1, cycle, ct);
                    pages[^1] = cycle;
                    await WriteAcquiringManifestAsync(acquisitionRoot, requestKey, symbols, normalizedBenchmark,
                        from, to, startedAt, pagesBySymbol: null, failureReason: null, ct);
                    continue;
                }
                if (source is IHistoricalBarPageSource paged)
                {
                    await foreach (var page in paged.ReadPagesAsync(symbol, EasternOffset(from),
                                       EasternOffset(to.AddDays(1)), nextCursor, completedPages, visitedCursors, ct))
                    {
                        await WritePageAsync(acquisitionRoot, symbol, completedPages++, page, ct);
                        pages.Add(page);
                        await WriteAcquiringManifestAsync(acquisitionRoot, requestKey, symbols, normalizedBenchmark,
                            from, to, startedAt, pagesBySymbol: null, failureReason: null, ct);
                    }
                }
                else
                {
                    var read = await source.ReadAsync(symbol, EasternOffset(from), EasternOffset(to.AddDays(1)), ct);
                    var page = new HistoricalBarPage(EasternOffset(to.AddDays(1)).ToString("O"), null, read.Bars,
                        read.RawBarCount, read.ReachedRequestedStart, read.OldestBar, read.StopReason,
                        source.BarTimeConvention);
                    await WritePageAsync(acquisitionRoot, symbol, completedPages, page, ct);
                    pages.Add(page);
                    await WriteAcquiringManifestAsync(acquisitionRoot, requestKey, symbols, normalizedBenchmark,
                        from, to, startedAt, pagesBySymbol: null, failureReason: null, ct);
                }
                if (pages.LastOrDefault() is not { } completed || !Terminal(completed))
                    throw new InvalidDataException($"{symbol} 과거봉 페이지 원천이 종료 상태 없이 중단되었습니다.");
            }

            var allPages = await ReadAllPagesAsync(acquisitionRoot, symbols, ct);
            var reads = allPages.ToDictionary(x => x.Key, x => Aggregate(x.Value), StringComparer.OrdinalIgnoreCase);
            var fetchedAt = clock.GetUtcNow();
            var import = await new ReplayBackfill(source, clock).WriteAsync(acquisitionRoot, normalizedWatchlist,
                normalizedBenchmark, from, to, fetchedAt, reads, ct);
            var manifest = Manifest("", requestKey, symbols, normalizedBenchmark, from, to, startedAt,
                fetchedAt, import.DataStatus, null, allPages, import.Sources);
            var datasetId = await ContentHashAsync(manifest, acquisitionRoot, ct);
            manifest = manifest with { DatasetId = datasetId };
            await WriteAtomicAsync(Path.Combine(acquisitionRoot, "manifest.json"), manifest, ct);
            var datasetRoot = DatasetRoot(datasetId);
            if (!Directory.Exists(datasetRoot))
            {
                Directory.CreateDirectory(_root);
                Directory.Move(acquisitionRoot, datasetRoot);
            }
            else
            {
                Directory.Delete(acquisitionRoot, true);
                var existing = await TryOpenDatasetAsync(datasetId, contract, ct) ??
                    throw new InvalidDataException("동일 content hash의 기존 dataset 계약이 일치하지 않습니다.");
                manifest = existing.Manifest;
                import = existing.Import;
            }
            await WriteAtomicAsync(indexPath, new DatasetIndex(requestKey, datasetId, null), ct);
            ValidatedDatasets[datasetRoot] = Fingerprint(datasetRoot);
            return new ReplayDatasetLease(datasetId, datasetRoot, manifest, import, false);
        }
        catch (Exception exception)
        {
            if (Directory.Exists(acquisitionRoot))
                await WriteAcquiringManifestAsync(acquisitionRoot, requestKey, symbols, normalizedBenchmark, from, to,
                    startedAt, pagesBySymbol: null, exception is OperationCanceledException ? "canceled" : exception.Message,
                    CancellationToken.None);
            throw;
        }
    }

    public static string RequestKey(string provider, bool adjusted, DateOnly from, DateOnly to, string benchmark,
        IEnumerable<string> symbols)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            provider = provider.Trim(),
            adjusted,
            timeframe = Timeframe,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            benchmark = benchmark.Trim().ToUpperInvariant(),
            symbols = symbols.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    static ImmutableArray<string> NormalizeSymbols(IReadOnlyList<string> watchlist, string benchmark)
    {
        var symbols = watchlist.Append(benchmark).Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        foreach (var symbol in symbols) SafeSymbol(symbol);
        return symbols;
    }

    async Task<ReplayDatasetLease?> TryOpenDatasetAsync(string? datasetId, DatasetContract contract,
        CancellationToken ct)
    {
        if (!ValidDatasetId(datasetId)) return null;
        var root = DatasetRoot(datasetId!);
        var manifest = await ReadAsync<ReplayDatasetManifest>(Path.Combine(root, "manifest.json"), ct);
        var import = await ReadAsync<ReplayImportReport>(Path.Combine(root, "import-report.json"), ct);
        if (!ValidContract(datasetId!, root, manifest, import, contract)) return null;
        var fingerprint = Fingerprint(root);
        if (!ValidatedDatasets.TryGetValue(root, out var validated) || validated != fingerprint)
        {
            if (await ContentHashAsync(manifest!, root, ct) != datasetId) return null;
            ValidatedDatasets[root] = fingerprint;
        }
        return new ReplayDatasetLease(datasetId!, root, manifest!, import!, true);
    }

    async Task<ReplayDatasetLease?> FindDatasetAsync(DatasetContract contract, CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return null;
        ReplayDatasetLease? newest = null;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory) is "index" or "locks" or ".acquiring") continue;
            var datasetId = Path.GetFileName(directory);
            if (!ValidDatasetId(datasetId)) continue;
            var candidate = await TryOpenDatasetAsync(datasetId, contract, ct);
            if (candidate is not null && (newest is null || candidate.Manifest.FetchedAt > newest.Manifest.FetchedAt))
                newest = candidate;
        }
        return newest;
    }

    bool ValidContract(string datasetId, string root, ReplayDatasetManifest? manifest, ReplayImportReport? import,
        DatasetContract contract)
    {
        if (manifest is null || import is null || manifest.Status == "acquiring") return false;
        var expectedWatchlist = contract.Symbols.Where(x => x != contract.Benchmark).ToArray();
        var sourceSymbols = import.Sources.Select(x => x.Symbol).Order(StringComparer.Ordinal).ToArray();
        var progressSymbols = manifest.Progress.Select(x => x.Symbol).Order(StringComparer.Ordinal).ToArray();
        return Path.GetFileName(root) == datasetId && manifest.DatasetId == datasetId &&
               Directory.Exists(Path.Combine(root, "raw")) && Directory.Exists(Path.Combine(root, "bars")) &&
               manifest.SchemaVersion == SchemaVersion && manifest.RequestKey == contract.RequestKey &&
               manifest.Provider == contract.Provider && manifest.Adjusted == contract.Adjusted &&
               manifest.Timeframe == Timeframe && manifest.From == contract.From && manifest.To == contract.To &&
               manifest.Benchmark == contract.Benchmark && manifest.Symbols.SequenceEqual(contract.Symbols) &&
               manifest.Status is "available" or "partial" or "no-data" &&
               manifest.Status == import.DataStatus && manifest.FailureReason is null &&
               progressSymbols.SequenceEqual(contract.Symbols) &&
               import.Source == contract.Provider && import.Adjusted == contract.Adjusted &&
               import.From == contract.From && import.To == contract.To && import.Benchmark == contract.Benchmark &&
               import.Watchlist.SequenceEqual(expectedWatchlist) && sourceSymbols.SequenceEqual(contract.Symbols) &&
               manifest.Sources.SequenceEqual(import.Sources) &&
               import.FetchedAt == manifest.FetchedAt;
    }

    async Task WriteAcquiringManifestAsync(string root, string requestKey, ImmutableArray<string> symbols,
        string benchmark, DateOnly from, DateOnly to, DateTimeOffset startedAt,
        IReadOnlyDictionary<string, List<HistoricalBarPage>>? pagesBySymbol, string? failureReason,
        CancellationToken ct)
    {
        pagesBySymbol ??= await ReadAllPagesAsync(root, symbols, ct);
        var manifest = Manifest("", requestKey, symbols, benchmark, from, to, startedAt, null, "acquiring",
            failureReason, pagesBySymbol, []);
        await WriteAtomicAsync(Path.Combine(root, "manifest.json"), manifest, ct);
    }

    ReplayDatasetManifest Manifest(string datasetId, string requestKey, ImmutableArray<string> symbols,
        string benchmark, DateOnly from, DateOnly to, DateTimeOffset startedAt, DateTimeOffset? fetchedAt,
        string status, string? failureReason, IReadOnlyDictionary<string, List<HistoricalBarPage>> pages,
        ImmutableArray<ReplayImportSourceRow> sources) => new(SchemaVersion, datasetId, requestKey, source.Name,
        source.Adjusted, Timeframe, from, to, benchmark, symbols, startedAt, fetchedAt, status, failureReason,
        symbols.Select(symbol => Progress(symbol, pages.GetValueOrDefault(symbol) ?? [])).ToImmutableArray(), sources);

    static ReplayDatasetSymbolProgress Progress(string symbol, List<HistoricalBarPage> pages)
    {
        var last = pages.LastOrDefault();
        return new(symbol, pages.Count, pages.Sum(x => x.RawBarCount), last?.NextCursor,
            last is not null && Terminal(last), pages.Any(x => x.ReachedRequestedStart),
            pages.Select(x => x.OldestBar).Where(x => x.HasValue).Min(), last?.StopReason);
    }

    static HistoricalBarReadResult Aggregate(List<HistoricalBarPage> pages)
    {
        var bars = pages.SelectMany(x => x.Bars).ToArray();
        return new HistoricalBarReadResult(bars, pages.Sum(x => x.RawBarCount),
            pages.Any(x => x.ReachedRequestedStart), pages.Select(x => x.OldestBar).Where(x => x.HasValue).Min(),
            pages.LastOrDefault()?.StopReason);
    }

    static bool Terminal(HistoricalBarPage page) => page.ReachedRequestedStart || page.NextCursor is null ||
                                                     page.StopReason is not null;

    async Task<Dictionary<string, List<HistoricalBarPage>>> ReadAllPagesAsync(string root,
        IEnumerable<string> symbols, CancellationToken ct)
    {
        var result = new Dictionary<string, List<HistoricalBarPage>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols) result[symbol] = await ReadPagesAsync(root, symbol, ct);
        return result;
    }

    static async Task<List<HistoricalBarPage>> ReadPagesAsync(string root, string symbol, CancellationToken ct)
    {
        var directory = Path.Combine(root, "raw", SafeSymbol(symbol));
        if (!Directory.Exists(directory)) return [];
        var pages = new List<HistoricalBarPage>();
        foreach (var path in Directory.EnumerateFiles(directory, "page-*.json").Order(StringComparer.Ordinal))
            if (await ReadAsync<HistoricalBarPage>(path, ct) is { } page) pages.Add(page);
        return pages;
    }

    static async Task WritePageAsync(string root, string symbol, int index, HistoricalBarPage page,
        CancellationToken ct)
    {
        var path = Path.Combine(root, "raw", SafeSymbol(symbol), $"page-{index:D6}.json");
        await WriteAtomicAsync(path, page, ct);
    }

    public static bool IsValidSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var normalized = symbol.Trim().ToUpperInvariant();
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
            "LPT8", "LPT9" };
        return normalized.Length is > 0 and <= 32 && normalized is not "." and not ".." &&
               !normalized.EndsWith('.') && !reserved.Contains(normalized, StringComparer.Ordinal) &&
               normalized.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '-' or '_');
    }

    static string SafeSymbol(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (!IsValidSymbol(normalized))
            throw new ArgumentException("replay dataset 심볼에 경로로 사용할 수 없는 문자가 있습니다.", nameof(symbol));
        return normalized;
    }

    static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
    }

    static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json), ct);
        File.Move(temporary, path, true);
    }

    static async Task<FileStream> AcquireFileLockAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(50, ct); }
        }
    }

    static async Task<string> ContentHashAsync(ReplayDatasetManifest manifest, string root, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(manifest with { DatasetId = "" }, Json));
        hash.AppendData("\n"u8);
        foreach (var path in DatasetContentFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).Replace('\\', '/') + "\n"));
            hash.AppendData(await File.ReadAllBytesAsync(path, ct));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    static DatasetFingerprint Fingerprint(string root)
    {
        var files = DatasetFingerprintFiles(root).Select(path => new FileInfo(path)).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file.FullName).Replace('\\', '/') +
                                                   "\n" + file.Length + "\n" +
                                                   file.LastWriteTimeUtc.Ticks + "\n"));
            if (string.Equals(file.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                hash.AppendData(File.ReadAllBytes(file.FullName));
        }
        return new(files.Length, files.Sum(x => x.Length), Convert.ToHexString(hash.GetHashAndReset()));
    }

    static IEnumerable<string> DatasetFingerprintFiles(string root)
    {
        foreach (var path in DatasetContentFiles(root)) yield return path;
        var manifest = Path.Combine(root, "manifest.json");
        if (File.Exists(manifest)) yield return manifest;
    }

    static IEnumerable<string> DatasetContentFiles(string root)
    {
        foreach (var directoryName in new[] { "raw", "bars" })
        {
            var directory = Path.Combine(root, directoryName);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Order(StringComparer.OrdinalIgnoreCase)) yield return path;
        }
        var import = Path.Combine(root, "import-report.json");
        if (File.Exists(import)) yield return import;
    }

    string AcquisitionRoot(string id)
    {
        if (!ValidAcquisitionId(id)) throw new InvalidDataException("유효하지 않은 replay acquisition ID입니다.");
        return Path.Combine(_root, ".acquiring", id);
    }

    string DatasetRoot(string id)
    {
        if (!ValidDatasetId(id)) throw new InvalidDataException("유효하지 않은 replay dataset ID입니다.");
        return Path.Combine(_root, id);
    }

    static bool ValidDatasetId(string? id) => id is { Length: 64 } &&
        id.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool ValidAcquisitionId(string? id, string? requestKey = null)
    {
        if (id is not { Length: 97 } || id[64] != '-' || !ValidDatasetId(id[..64]) ||
            !id[65..].All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return requestKey is null || id.StartsWith(requestKey + "-", StringComparison.Ordinal);
    }
    static DateTimeOffset EasternOffset(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Eastern.GetUtcOffset(local));
    }

    sealed record DatasetIndex(string RequestKey, string? DatasetId, string? AcquisitionId);
    sealed record DatasetContract(string RequestKey, string Provider, bool Adjusted, DateOnly From, DateOnly To,
        string Benchmark, ImmutableArray<string> Symbols);
    readonly record struct DatasetFingerprint(int Files, long Bytes, string MetadataHash);
}
