using System.Text;
using System.Text.Json;

namespace Astra.Server.Application;

// 이슈 #131 — 관측 jsonl 읽기의 단일 창구.
// #28 검증 보고서와 #131 실매매 대조가 같은 파일을 읽으므로 파싱을 두 벌 두지 않는다.
// 여기서는 저장 형식만 알고 해석은 하지 않는다 — 손상된 줄은 버리되 세지 않고 넘기지 않는다(§16).

/// <summary>관측 파일 한 줄. <see cref="Record"/>가 null이면 해석 실패다(#131).</summary>
public sealed record ObservationLogLine(string Raw, int Bytes, StructureObservationRecord? Record);

/// <summary>관측 파일 하루치. 파일이 없다는 사실도 결과의 일부다(#131).</summary>
public sealed record ObservationLogDay(string File, DateOnly TradingDate, bool Found,
    IReadOnlyList<ObservationLogLine> Lines);

/// <summary>거래일 하루당 파일 하나를 읽어 레코드로 해석한다(#131).</summary>
public sealed class ObservationLogReader(IStructureObservationStore store)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ObservationLogDay> ReadDayAsync(DateOnly date, CancellationToken ct)
    {
        var file = StructureObservationWriter.FileName(date);
        var content = await store.ReadLinesAsync(file, ct);
        if (content.Count == 0) return new ObservationLogDay(file, date, false, []);

        var lines = new List<ObservationLogLine>(content.Count);
        foreach (var line in content)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lines.Add(new ObservationLogLine(line, Encoding.UTF8.GetByteCount(line) + 1, Parse(line)));
        }
        return new ObservationLogDay(file, date, true, lines);
    }

    public async Task<IReadOnlyList<ObservationLogDay>> ReadRangeAsync(DateOnly from, DateOnly to,
        CancellationToken ct)
    {
        var days = new List<ObservationLogDay>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            days.Add(await ReadDayAsync(date, ct));
        }
        return days;
    }

    /// <summary>식별자·심볼이 없는 줄은 해석 실패로 본다 — 연결 키가 없으면 어떤 집계에도 쓸 수 없다.</summary>
    static StructureObservationRecord? Parse(string line)
    {
        try
        {
            var record = JsonSerializer.Deserialize<StructureObservationRecord>(line, Json);
            return record is null || string.IsNullOrEmpty(record.ObservationId) || string.IsNullOrEmpty(record.Symbol)
                ? null
                : record;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
