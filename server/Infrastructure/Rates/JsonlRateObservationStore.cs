using System.Globalization;
using System.Text;
using System.Text.Json;
using Astra.Server.Application.Rates;

namespace Astra.Server.Infrastructure.Rates;

/// <summary>`App_Data/rates/<yyyy-MM-dd>.jsonl` append-only 기록. 날짜는 수집 시각의 UTC 기준이다(#316).</summary>
public sealed class JsonlRateObservationStore(string root) : IRateObservationStore
{
    const string DateFormat = "yyyy-MM-dd";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string _root = Path.GetFullPath(root);
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(IReadOnlyCollection<RateObservation> observations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            foreach (var group in observations.GroupBy(x => DateOnly.FromDateTime(x.At.UtcDateTime)))
            {
                var builder = new StringBuilder();
                foreach (var observation in group.OrderBy(x => x.At))
                    builder.Append(JsonSerializer.Serialize(observation, Json)).Append('\n');
                await using var stream = new FileStream(PathFor(group.Key), FileMode.Append, FileAccess.Write,
                    FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(builder.ToString()), ct);
                await stream.FlushAsync(ct);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RateObservation>> ReadDayAsync(DateOnly day, CancellationToken ct)
    {
        var path = PathFor(day);
        if (!File.Exists(path)) return [];
        var rows = new List<RateObservation>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<RateObservation>(line, Json) is { Tenor.Length: > 0 } row) rows.Add(row);
            }
            catch (JsonException) { }
        }
        return rows;
    }

    public async Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-Math.Max(1, retentionDays));
        await _gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_root)) return 0;
            var deleted = 0;
            foreach (var path in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.TopDirectoryOnly).ToArray())
            {
                if (FileDate(path) is not { } date || date >= cutoff) continue;
                File.Delete(path);
                deleted++;
            }
            return deleted;
        }
        finally { _gate.Release(); }
    }

    string PathFor(DateOnly day) => Path.Combine(_root, day.ToString(DateFormat, CultureInfo.InvariantCulture) + ".jsonl");

    static DateOnly? FileDate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
