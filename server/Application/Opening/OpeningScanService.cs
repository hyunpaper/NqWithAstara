using System.Collections.Concurrent;
using System.Text.Json;
using Astra.Server.Domain;
using Astra.Server.Domain.Opening;

namespace Astra.Server.Application.Opening;

/// <summary>개장 초반 스캔의 런타임 상태·관측 기록(§4.3, #371). 폴링 훅 값만으로 계산하며 Toss 조회·진입 판정에 쓰이지 않는다.</summary>
public sealed class OpeningScanService(OpeningScanPolicy policy, OpeningVolumeProfileSource profiles,
    OpeningScanObservationWriter writer, TimeProvider clock, IMonitorDiagnostics diagnostics)
{
    public const string RecordVersion = "opening-scan.1";
    public const int RefreshSeconds = 60;
    public const int SnapshotGraceMinutes = 5;
    public const int FollowupMinute = 35;
    public const int AnchorMinute = 5;
    public const int SummaryTopN = 10;

    sealed class SymbolState
    {
        public string Name = "";
        public OpeningScanSnapshot? Latest;
        public OpeningScanRowDto? At5Row;
        public OpeningScanRowDto? At30Row;
        public string GradeAt5 = OpeningSnapshotEvaluator.GradeWeak;
        public double ScoreAt5;
        public double? Rvol5;
        public int LastRecordedMinute;
        public double? AnchorClose;
        public bool Followup30Written;
        public OpeningFollowupRowDto? Followup;
        public bool CloseWritten;
        public OpeningCloseRowDto? Close;
        public double? LastQuote;
        public DateTimeOffset? LastQuoteAt;
        public int SampleCount;
        public OpeningProfileStats Stats = new(0, 0, 0, 0);
        public bool ProfilesLoaded;
    }

    sealed class SessionState(DateOnly date, DateTimeOffset open, DateTimeOffset end)
    {
        public DateOnly Date { get; } = date;
        public DateTimeOffset Open { get; } = open;
        public DateTimeOffset End { get; } = end;
        public ConcurrentDictionary<string, SymbolState> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    readonly SemaphoreSlim _sessionGate = new(1, 1);
    volatile SessionState? _session;
    DateTimeOffset? _lastObservedAt;
    string? _lastError;

    public async Task ObserveAsync(WatchItem item, IReadOnlyList<Candle> all, IReadOnlyList<Candle> bars,
        (double Price, DateTimeOffset At) quote, bool validQuote, MarketSession market,
        IReadOnlyList<Candle>? daily, CancellationToken ct)
    {
        try
        {
            if (!policy.Enabled || market.Start is not { } start || market.End is not { } end) return;
            var sessionDate = MarketRules.TradingDate(start);
            await EnsureSessionAsync(sessionDate, start, end, ct);
            var session = _session;
            if (session is null || session.Date != sessionDate) return;

            var open = start;
            var sym = session.Symbols.GetOrAdd(item.Symbol, _ => new SymbolState());
            sym.Name = item.Name;
            if (validQuote) { sym.LastQuote = quote.Price; sym.LastQuoteAt = quote.At; }

            if (!sym.ProfilesLoaded)
            {
                var loaded = await profiles.GetAsync(item.Symbol, sessionDate, policy.LookbackSessions, ct);
                sym.Stats = loaded.Stats;
                sym.ProfilesLoaded = true;
            }
            var previous = (await profiles.GetAsync(item.Symbol, sessionDate, policy.LookbackSessions, ct)).Profiles;

            var now = clock.GetLocalNow();
            var windowEnd = open.AddMinutes(policy.WindowMinutes);
            var windowBars = bars.Where(x => x.Timestamp >= open && x.Timestamp < windowEnd).OrderBy(x => x.Timestamp).ToArray();

            // 앵커(k=5 봉 종가)는 스냅샷 타이밍과 무관하게 해당 봉에서 직접 고정한다 — 사후 수익률 기준가다.
            var anchorBar = bars.FirstOrDefault(x => x.Timestamp == open.AddMinutes(AnchorMinute - 1));
            if (anchorBar is not null && sym.AnchorClose is null) sym.AnchorClose = anchorBar.Close;

            if (windowBars.Length > 0 && now < windowEnd.AddMinutes(SnapshotGraceMinutes))
            {
                var input = new OpeningScanInput(item.Symbol, item.Name, sessionDate, open, now, windowBars, all, daily,
                    previous, quote.Price, quote.At,
                    validQuote ? OpeningSnapshotEvaluator.QuoteFresh : OpeningSnapshotEvaluator.QuoteStale, policy);
                var snapshot = OpeningSnapshotEvaluator.Evaluate(input);
                sym.Latest = snapshot;
                sym.SampleCount = snapshot.SampleCount;
                if (snapshot.ElapsedMinutes >= AnchorMinute && sym.At5Row is null)
                {
                    sym.At5Row = OpeningScanDtoMapper.Row(snapshot);
                    sym.GradeAt5 = snapshot.Grade;
                    sym.ScoreAt5 = snapshot.Score;
                    sym.Rvol5 = snapshot.RvolNow;
                }
                if (snapshot.ElapsedMinutes >= AnchorMinute) sym.At30Row = OpeningScanDtoMapper.Row(snapshot);
                if (snapshot.ElapsedMinutes > sym.LastRecordedMinute)
                {
                    await writer.AppendAsync(sessionDate, SnapshotId(item.Symbol, sessionDate, snapshot.ElapsedMinutes),
                        OpeningScanObservationWriter.KindSnapshot, SnapshotRecord(snapshot), ct);
                    sym.LastRecordedMinute = snapshot.ElapsedMinutes;
                }
            }

            await MaybeWriteFollowupAsync(session, sym, item, bars, ct);
            _lastObservedAt = now;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            diagnostics.MarketDataFailed(item.Symbol, "opening-scan", ex);
        }
    }

    async Task MaybeWriteFollowupAsync(SessionState session, SymbolState sym, WatchItem item,
        IReadOnlyList<Candle> bars, CancellationToken ct)
    {
        if (sym.Followup30Written || sym.AnchorClose is not { } anchor || anchor <= 0) return;
        var open = session.Open;
        var bar35 = bars.FirstOrDefault(x => x.Timestamp == open.AddMinutes(FollowupMinute - 1));
        if (bar35 is null) return;
        var window = bars.Where(x => x.Timestamp >= open.AddMinutes(AnchorMinute) && x.Timestamp < open.AddMinutes(FollowupMinute)).ToArray();
        double? mfe = window.Length == 0 ? null : Round2((window.Max(x => x.High) / anchor - 1) * 100);
        double? mae = window.Length == 0 ? null : Round2((window.Min(x => x.Low) / anchor - 1) * 100);
        var ret = Round2((bar35.Close / anchor - 1) * 100);
        var record = new OpeningFollowup30Record(FollowupId(item.Symbol, session.Date), OpeningScanObservationWriter.KindFollowup30,
            RecordVersion, item.Symbol, item.Name, session.Date, AnchorMinute, Round4(anchor), Round4(bar35.Close), ret, mfe, mae,
            sym.GradeAt5, sym.ScoreAt5, sym.Rvol5);
        await writer.AppendAsync(session.Date, record.ObservationId, OpeningScanObservationWriter.KindFollowup30, record, ct);
        sym.Followup = new OpeningFollowupRowDto(item.Symbol, item.Name, sym.GradeAt5, ret, mfe, mae, sym.Rvol5);
        sym.Followup30Written = true;
    }

    public async Task FinalizeSessionAsync(MarketSession market, CancellationToken ct)
    {
        try
        {
            var session = _session;
            if (session is null) return;
            foreach (var (symbol, sym) in session.Symbols)
            {
                if (sym.CloseWritten) continue;
                ct.ThrowIfCancellationRequested();
                var warnings = sym.AnchorClose is null ? new[] { "ANCHOR_MISSING" } : [];
                double? ret = sym.AnchorClose is { } anchor && anchor > 0 && sym.LastQuote is { } q
                    ? Round2((q / anchor - 1) * 100) : null;
                var record = new OpeningCloseRecord(CloseId(symbol, session.Date), OpeningScanObservationWriter.KindClose,
                    RecordVersion, symbol, sym.Name, session.Date, sym.AnchorClose is null ? null : Round4(sym.AnchorClose.Value),
                    sym.LastQuote, sym.LastQuoteAt, ret, sym.GradeAt5, warnings);
                await writer.AppendAsync(session.Date, record.ObservationId, OpeningScanObservationWriter.KindClose, record, ct);
                sym.Close = new OpeningCloseRowDto(symbol, sym.Name, sym.GradeAt5, ret);
                sym.CloseWritten = true;
            }
        }
        catch (Exception ex) { _lastError = ex.Message; diagnostics.PollFailed("opening-scan-finalize", ex); }
    }

    async Task EnsureSessionAsync(DateOnly sessionDate, DateTimeOffset open, DateTimeOffset end, CancellationToken ct)
    {
        if (_session?.Date == sessionDate) return;
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_session?.Date == sessionDate) return;
            profiles.ResetBefore(sessionDate);
            var session = new SessionState(sessionDate, open, end);
            await RestoreAsync(session, ct);
            _session = session;
        }
        finally { _sessionGate.Release(); }
    }

    async Task RestoreAsync(SessionState session, CancellationToken ct)
    {
        foreach (var line in await writer.ReadAsync(session.Date, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var kind = document.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
                switch (kind)
                {
                    case OpeningScanObservationWriter.KindSnapshot: RestoreSnapshot(session, line); break;
                    case OpeningScanObservationWriter.KindFollowup30: RestoreFollowup(session, line); break;
                    case OpeningScanObservationWriter.KindClose: RestoreClose(session, line); break;
                }
            }
            catch (JsonException) { }
        }
    }

    void RestoreSnapshot(SessionState session, string line)
    {
        var record = JsonSerializer.Deserialize<OpeningSnapshotRecord>(line, Json);
        if (record is null) return;
        var sym = session.Symbols.GetOrAdd(record.Symbol, _ => new SymbolState());
        sym.Name = record.Name;
        var row = SnapshotRowFrom(record);
        if (record.ElapsedMinutes > sym.LastRecordedMinute) sym.LastRecordedMinute = record.ElapsedMinutes;
        if (record.ElapsedMinutes >= AnchorMinute)
        {
            if (sym.At5Row is null)
            {
                sym.At5Row = row;
                sym.GradeAt5 = record.Grade;
                sym.ScoreAt5 = record.Score;
                sym.Rvol5 = record.RvolNow;
                sym.AnchorClose = record.LastBarClose;
            }
            sym.At30Row = row;
        }
    }

    void RestoreFollowup(SessionState session, string line)
    {
        var record = JsonSerializer.Deserialize<OpeningFollowup30Record>(line, Json);
        if (record is null) return;
        var sym = session.Symbols.GetOrAdd(record.Symbol, _ => new SymbolState());
        sym.Name = record.Name;
        sym.Followup = new OpeningFollowupRowDto(record.Symbol, record.Name, record.GradeAt5, record.ReturnPercent30,
            record.MfePercent30, record.MaePercent30, record.Rvol5);
        sym.Followup30Written = true;
        sym.AnchorClose ??= record.AnchorBarClose;
    }

    void RestoreClose(SessionState session, string line)
    {
        var record = JsonSerializer.Deserialize<OpeningCloseRecord>(line, Json);
        if (record is null) return;
        var sym = session.Symbols.GetOrAdd(record.Symbol, _ => new SymbolState());
        sym.Name = record.Name;
        sym.Close = new OpeningCloseRowDto(record.Symbol, record.Name, record.GradeAt5, record.ReturnPercentClose);
        sym.CloseWritten = true;
    }

    public OpeningScanResponse Query()
    {
        var now = clock.GetUtcNow();
        if (!policy.Enabled)
            return Empty("disabled", now);
        var session = _session;
        if (session is null) return Empty("idle", now);

        var today = MarketRules.TradingDate(now);
        var afterWindow = now >= session.Open.AddMinutes(policy.WindowMinutes);
        if (session.Date != today && afterWindow) return Empty("idle", now);

        var elapsed = Math.Clamp((int)(now - session.Open).TotalMinutes, 0, policy.WindowMinutes);
        if (afterWindow)
        {
            var summary = BuildSummary(session);
            return new OpeningScanResponse(session.Date.ToString("yyyy-MM-dd"), "summary", session.Open,
                session.Open.AddMinutes(policy.WindowMinutes), now, policy.WindowMinutes, policy.Version,
                policy.LookbackSessions, policy.MinimumSessions, RefreshSeconds, [], summary, []);
        }

        var rows = session.Symbols.Values.Where(x => x.Latest is not null)
            .Select(x => OpeningScanDtoMapper.Row(x.Latest!))
            .OrderBy(x => OpeningScanDtoMapper.GradeRank(x.Grade)).ThenByDescending(x => x.Score)
            .ToArray();
        var phase = rows.Length > 0 ? "scanning" : "pending";
        return new OpeningScanResponse(session.Date.ToString("yyyy-MM-dd"), phase, session.Open,
            session.Open.AddMinutes(policy.WindowMinutes), now, elapsed, policy.Version,
            policy.LookbackSessions, policy.MinimumSessions, RefreshSeconds, rows, null, []);
    }

    OpeningScanSummaryDto BuildSummary(SessionState session)
    {
        OpeningScanRowDto[] Top(Func<SymbolState, OpeningScanRowDto?> pick) => session.Symbols.Values
            .Select(pick).Where(x => x is not null).Select(x => x!)
            .OrderBy(x => OpeningScanDtoMapper.GradeRank(x.Grade)).ThenByDescending(x => x.Score)
            .Take(SummaryTopN).ToArray();
        var followup = session.Symbols.Values.Select(x => x.Followup).Where(x => x is not null).Select(x => x!)
            .OrderBy(x => OpeningScanDtoMapper.GradeRank(x.GradeAt5)).ThenByDescending(x => x.ReturnPercent30 ?? double.MinValue)
            .Take(SummaryTopN).ToArray();
        var close = session.Symbols.Values.Select(x => x.Close).Where(x => x is not null).Select(x => x!)
            .OrderBy(x => OpeningScanDtoMapper.GradeRank(x.GradeAt5)).ThenByDescending(x => x.ReturnPercentClose ?? double.MinValue)
            .Take(SummaryTopN).ToArray();
        return new OpeningScanSummaryDto(Top(x => x.At5Row), Top(x => x.At30Row), followup, close);
    }

    OpeningScanResponse Empty(string phase, DateTimeOffset now) => new(null, phase, null, null, now, 0, policy.Version,
        policy.LookbackSessions, policy.MinimumSessions, RefreshSeconds, [], null, []);

    public object Health()
    {
        var session = _session;
        var counts = writer.Counts;
        var now = clock.GetUtcNow();
        var phase = Query().Phase;
        var loadedEntries = session?.Symbols.Where(x => x.Value.ProfilesLoaded)
            .OrderBy(x => x.Key, StringComparer.Ordinal).ToArray() ?? [];
        var loaded = loadedEntries.Select(x => x.Value).ToArray();
        return new
        {
            enabled = policy.Enabled,
            status = phase,
            policyVersion = policy.Version,
            sessionDate = session?.Date.ToString("yyyy-MM-dd"),
            symbols = session?.Symbols.Count ?? 0,
            profiles = new
            {
                loadedSymbols = loaded.Length,
                sessionsMin = loaded.Length == 0 ? 0 : loaded.Min(x => x.Stats.Sessions),
                sessionsMax = loaded.Length == 0 ? 0 : loaded.Max(x => x.Stats.Sessions),
                insufficientSymbols = loaded.Count(x => x.SampleCount < policy.GradeMinimumSessions),
                tossSymbols = loaded.Count(x => x.Stats.Source == "toss"),
                tossFailures = loaded.Sum(x => x.Stats.TossFailures),
                lastRefresh = loaded.Select(x => x.Stats.LoadedAt).Where(x => x is not null).DefaultIfEmpty(null).Max(),
                legacyShiftedSessions = loaded.Sum(x => x.Stats.LegacyShifted),
                rejectedSessions = loaded.Sum(x => x.Stats.Rejected),
                incompleteSessions = loaded.Sum(x => x.Stats.Incomplete),
                bySymbol = loadedEntries.Select(x => new
                {
                    symbol = x.Key,
                    sessions = x.Value.Stats.Sessions,
                    source = x.Value.Stats.Source,
                    tossFailures = x.Value.Stats.TossFailures,
                    loadedAt = x.Value.Stats.LoadedAt,
                }).ToArray(),
            },
            records = new
            {
                snapshots = counts.Snapshots,
                followup30 = counts.Followup30,
                close = counts.Close,
                duplicatesSuppressed = counts.DuplicatesSuppressed,
            },
            lastObservedAt = _lastObservedAt,
            lastError = _lastError,
        };
    }

    static OpeningScanRowDto SnapshotRowFrom(OpeningSnapshotRecord r) => new(r.Symbol, r.Name, r.Grade, r.Score,
        r.VolumeStatus, r.RvolNow, r.Rvol3, r.Rvol5, r.Rvol20, r.SampleCount, r.ChangeFromOpenPercent, r.ChangeFromPrevClosePercent,
        r.GapPercent, r.PrevCloseSource, r.AboveVwap, r.First5, r.BrokeOpeningRange, r.Premarket, r.QuoteStatus,
        r.Reasons, r.ObservedAt, r.ElapsedMinutes);

    OpeningSnapshotRecord SnapshotRecord(OpeningScanSnapshot s) => new(
        SnapshotId(s.Symbol, s.SessionDate, s.ElapsedMinutes), OpeningScanObservationWriter.KindSnapshot, RecordVersion,
        policy.Version, policy.PolicyHash, s.Symbol, s.Name, s.SessionDate, s.SessionOpen, s.ObservedAt, s.ElapsedMinutes,
        s.LastBarStart, s.LastBarClose, s.QuotePrice, s.QuoteAt, s.QuoteStatus, s.CumulativeVolume, s.RvolNow, s.Rvol3, s.Rvol5, s.Rvol20,
        s.BaselineMean, s.BaselineMedian, s.SampleCount, s.VolumeStatus, s.PrevClose, s.PrevCloseSource, s.Open,
        s.OpenBarMissing, s.GapPercent, s.ChangeFromPrevClosePercent, s.ChangeFromOpenPercent, s.Vwap, s.AboveVwap,
        s.First5 is null ? null : new OpeningFirst5Dto(s.First5.BarsSeen, s.First5.UpBars, s.First5.NewHighs),
        s.OpeningRangeHigh5, s.BrokeOpeningRange,
        s.Premarket is null ? null : new OpeningPremarketDto(s.Premarket.High, s.Premarket.Volume, s.Premarket.AbovePremarketHigh),
        s.Grade, s.Score, s.Reasons, s.Warnings);

    static string SnapshotId(string symbol, DateOnly date, int minute) => $"{symbol}:{date:yyyy-MM-dd}:{minute}";
    static string FollowupId(string symbol, DateOnly date) => $"{symbol}:{date:yyyy-MM-dd}:f30";
    static string CloseId(string symbol, DateOnly date) => $"{symbol}:{date:yyyy-MM-dd}:close";

    static double Round2(double value) => double.IsFinite(value) ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : 0;
    static double Round4(double value) => double.IsFinite(value) ? Math.Round(value, 4, MidpointRounding.AwayFromZero) : 0;
}
