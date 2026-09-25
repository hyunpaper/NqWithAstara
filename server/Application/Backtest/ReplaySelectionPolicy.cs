namespace Astra.Server.Application.Backtest;

public static class ReplaySelectionPolicy
{
    public const string Version = "walk-forward-frequency-gate.v1";
    public const int MinimumTrainingRows = 20;
    public const int SymbolSessionsPerRequiredEntry = 20;

    public static int RequiredTrainingRows(
        IEnumerable<HistoricalStructureTradeReplay.ReplaySourceCoverage> coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var symbolSessions = coverage.Sum(x => x.Sessions);
        var frequencyRows = (int)Math.Ceiling((double)symbolSessions / SymbolSessionsPerRequiredEntry);
        return Math.Max(MinimumTrainingRows, frequencyRows);
    }
}
