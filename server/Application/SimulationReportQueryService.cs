namespace Astra.Server.Application;

public sealed record SimulationStats(int Total, int Open, int Closed, int Wins, double? WinRate, double? AvgPnl, double? TotalPnl, int ValidClosed, int MissingPnl, int EstimatedExits);
public sealed record SimulationProfile(int Count, double? Score, double? ExtSigma, double? RelVolume, double? BuyShare, double? Rsi);
public sealed record SimulationAnalysis(DateTimeOffset GeneratedAt, int ClosedCount, double WinRate, int TargetWinRate, double? AvgWin, double? AvgLoss, double Expectancy, object ExitShare, SimulationProfile WinnerProfile, SimulationProfile LoserProfile, string[] Insights, string SampleWarning);
public sealed record SimulationKindReport(string Kind, SimulationStats Stats);
public sealed record SimulationVersionReport(string Version, string Label, SimulationStats Stats, SimulationAnalysis? Analysis, IEnumerable<SimulationKindReport> ByKind);
public sealed record SimulationReport(SimulationStats Summary, IEnumerable<SimulationKindReport> ByKind, SimulationAnalysis? Analysis, IEnumerable<SimulationVersionReport> ByVersion, IEnumerable<SimTrade> Trades);

public sealed class SimulationReportQueryService(ILocalStore store, TimeProvider clock)
{
    public async Task<SimulationReport> GetAsync()
    {
        var trades = await store.Read("simtrades.json", new List<SimTrade>());
        var versions = trades.GroupBy(VersionOf).OrderByDescending(g => g.Max(x => x.EnteredAt)).Select(g =>
            new SimulationVersionReport(g.Key, g.Key == "legacy" ? "Legacy / unknown" : g.Key, Stats(g), Analysis(g.ToArray()), g.GroupBy(x => x.Kind).Select(k => new SimulationKindReport(k.Key, Stats(k))).ToArray())).ToArray();
        var rootAnalysis = versions.Length <= 1 ? Analysis(trades) : null;
        return new(Stats(trades), trades.GroupBy(x => x.Kind).Select(g => new SimulationKindReport(g.Key, Stats(g))), rootAnalysis, versions, trades.OrderByDescending(x => x.EnteredAt).Take(200));
    }

    static string VersionOf(SimTrade trade) => string.IsNullOrWhiteSpace(trade.Logic) ? "legacy" : trade.Logic.Trim();
    static SimulationStats Stats(IEnumerable<SimTrade> source)
    {
        var all = source.ToArray(); var closed = all.Where(x => x.Status != "OPEN").ToArray(); var valid = closed.Where(x => x.PnlPercent is { } pnl && double.IsFinite(pnl)).ToArray(); var wins = valid.Count(x => x.PnlPercent > 0);
        return new(all.Length, all.Count(x => x.Status == "OPEN"), closed.Length, wins,
            valid.Length == 0 ? null : Math.Round(wins * 100d / valid.Length, 1),
            valid.Length == 0 ? null : Math.Round(valid.Average(x => x.PnlPercent!.Value), 2),
            valid.Length == 0 ? null : Math.Round(valid.Sum(x => x.PnlPercent!.Value), 2), valid.Length, closed.Length - valid.Length, closed.Count(x => x.ExitEstimated == true));
    }

    SimulationAnalysis? Analysis(IReadOnlyCollection<SimTrade> source)
    {
        var closed = source.Where(x => x.Status != "OPEN" && x.PnlPercent is { } pnl && double.IsFinite(pnl)).ToArray();
        if (closed.Length < 10) return null;
        var wins = closed.Where(x => x.PnlPercent > 0).ToArray(); var losses = closed.Where(x => x.PnlPercent <= 0).ToArray();
        static double? Avg(IEnumerable<SimTrade> rows, Func<SimTrade, double?> pick) { var values = rows.Select(pick).OfType<double>().ToArray(); return values.Length == 0 ? null : Math.Round(values.Average(), 2); }
        static SimulationProfile Profile(SimTrade[] rows) => new(rows.Length, Avg(rows, x => x.Score), Avg(rows, x => x.ExtSigma), Avg(rows, x => x.RelVolume), Avg(rows, x => x.BuyShare), Avg(rows, x => x.Rsi));
        var winRate = Math.Round(wins.Length * 100d / closed.Length, 1);
        var stop = closed.Count(x => x.Status == "STOP") * 100d / closed.Length; var cut = closed.Count(x => x.Status == "CUT") * 100d / closed.Length; var eod = closed.Count(x => x.Status == "EOD") * 100d / closed.Length;
        var insights = new List<string> { $"관측 승률 {winRate}% (청산 {closed.Length}건). 이 표본만으로 규칙 변경 효과나 원인을 확정할 수 없습니다." };
        var lExt = Avg(losses, x => x.ExtSigma); var wExt = Avg(wins, x => x.ExtSigma);
        if (lExt is not null && wExt is not null && Math.Abs(lExt.Value - wExt.Value) > .4) insights.Add($"손실/수익 진입의 평균 σ가 {lExt} / {wExt}로 관측됐습니다. 더 큰 표본에서 재확인하세요.");
        var lShare = Avg(losses, x => x.BuyShare); var wShare = Avg(wins, x => x.BuyShare);
        if (lShare is not null && wShare is not null && Math.Abs(lShare.Value - wShare.Value) > 8) insights.Add($"손실/수익 진입의 평균 매수비중이 {lShare}% / {wShare}%로 관측됐습니다. 인과관계로 해석할 수 없습니다.");
        foreach (var group in closed.GroupBy(x => x.Kind).Where(x => x.Count() >= 5)) insights.Add($"{group.Key} 관측 승률 {Math.Round(group.Count(x => x.PnlPercent > 0) * 100d / group.Count())}% ({group.Count()}건).");
        var warning = closed.Length < 30 ? "소표본 결과입니다. 다음 독립 세션의 같은 로직 버전에서 재현되는지 확인하세요." : "관측 결과이며 시장 국면과 종목 구성의 영향을 포함합니다.";
        return new(clock.GetLocalNow(), closed.Length, winRate, 80, Avg(wins, x => x.PnlPercent), Avg(losses, x => x.PnlPercent), Math.Round(closed.Average(x => x.PnlPercent!.Value), 2),
            new { stop = Math.Round(stop), cut = Math.Round(cut), eod = Math.Round(eod), target = Math.Round(100 - stop - cut - eod) }, Profile(wins), Profile(losses), insights.ToArray(), warning);
    }
}
