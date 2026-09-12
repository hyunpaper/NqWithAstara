using Astra.Server.Domain;
using Astra.Server.Domain.Validation;

namespace Astra.Server.Application;

// 이슈 #131 — 실체결 vs v5 대조 보고서 조회(Application 절반).
// 읽기 전용이다. 저장된 실체결 파일·관측 jsonl·simtrades를 읽어 Domain 순수 함수에 넘길 뿐이며
// 어떤 파일에도 쓰지 않고 운영 진입·청산 정책을 건드리지 않는다.

/// <summary>대조 보고서 응답. 실체결 파일이 없다는 사실도 결과의 일부다(#131).</summary>
public sealed record RealVsV5Response(DateOnly TradingDate, bool FillsCollected, DateTimeOffset? CollectedAt,
    int ObservationLines, bool ObservationFileFound, RealVsV5Report Report, IReadOnlyList<string> Limitations);

public sealed class RealVsV5QueryService(ILocalStore store, IStructureObservationStore observations,
    RealFillsService fills, TimeProvider clock)
{
    /// <summary>실체결 파일이 없어 대조 자체가 불가능한 상태. 0건 성과로 바꾸지 않는다.</summary>
    public const string NoFillsCollected = "REAL_FILLS_NOT_COLLECTED";

    /// <summary>그날 관측 파일이 없어 v5 쪽 근거가 없는 상태(보존 한계의 증거).</summary>
    public const string NoObservations = "REAL_FILLS_NO_OBSERVATIONS";

    readonly ObservationLogReader _reader = new(observations);

    public async Task<(int HttpStatus, RealVsV5Response? Response)> GetAsync(string? date, CancellationToken ct)
    {
        DateOnly tradingDate;
        if (string.IsNullOrWhiteSpace(date)) tradingDate = MarketRules.TradingDate(clock.GetUtcNow());
        else if (!DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out tradingDate))
            return (400, null);

        var day = await fills.ReadAsync(tradingDate, ct);
        var observed = await _reader.ReadDayAsync(tradingDate, ct);
        var trades = await store.Read("simtrades.json", new List<SimTrade>());

        var candidates = observed.Lines.Select(x => x.Record).OfType<StructureObservationRecord>()
            .SelectMany(Candidates).ToArray();
        var report = RealFillComparer.Compare(tradingDate, day?.Fills.Select(Fill).ToArray() ?? [], candidates,
            trades.Where(x => x.Structure is not null).Select(Trade).ToArray());

        var limitations = new List<string>();
        if (day is null) limitations.Add(NoFillsCollected);
        if (!observed.Found) limitations.Add(NoObservations);

        return (200, new RealVsV5Response(tradingDate, day is not null, day?.CollectedAt, observed.Lines.Count,
            observed.Found, report, limitations));
    }

    static RealFill Fill(RealFillRecord record) =>
        new(record.Symbol, record.Side, record.FilledAt, record.AverageFilledPrice, record.FilledQuantity,
            record.Commission, record.OrderType);

    /// <summary>관측 한 줄의 후보를 대조 입력 행으로 편다. ATR·추세는 관측 레코드의 추세 블록에서 온다.</summary>
    static IEnumerable<RealFillCandidateRow> Candidates(StructureObservationRecord record) =>
        (record.Candidates ?? []).Where(x => x is not null && !string.IsNullOrEmpty(x.EventId))
        .Select(x => new RealFillCandidateRow(record.Symbol, record.ObservedAt, x.EventId, x.Kind ?? string.Empty,
            x.State ?? string.Empty, x.EntryReference, record.Trend?.Atr1m, record.Trend?.State, x.EntryQuality,
            (IReadOnlyList<string>)(x.RejectionCodes ?? [])));

    static RealFillOpenTrade Trade(SimTrade trade) =>
        new(trade.Symbol, trade.EnteredAt, trade.ExitAt, (decimal)trade.Stop, (decimal)trade.Target);
}
