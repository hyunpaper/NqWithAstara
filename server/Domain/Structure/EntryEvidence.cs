using Astra.Server;
using System.Collections.Immutable;

namespace Astra.Server.Domain.Structure;

/// <summary>EntryQuality와 분리한 진입 시점 feature와 gate 증적이다. 승률로 해석하지 않는다.</summary>
public sealed record EntryEvidence(
    DateTimeOffset SignalAt,
    DateTimeOffset ConfirmationAt,
    TradeSide Side,
    StrategyRegime Regime,
    double? TrendAlignment,
    double? DistanceToInvalidationAtr,
    decimal? NetR,
    double? ExpectedNetR,
    double? RelativeVolume,
    double? VwapDistanceAtr,
    decimal? ZoneDistance,
    TimeSpan TimeOfDay,
    bool CostComplete,
    bool ExecutionReady,
    ImmutableArray<string> GateReasons,
    ImmutableArray<string> FeatureContributions,
    ConditionalReturnForecast? Forecast = null,
    StrategyRegimeAssessment? RegimeAssessment = null)
{
    public string Fingerprint() => string.Join('|', StructureMath.Iso(SignalAt), StructureMath.Iso(ConfirmationAt),
        Side, Regime.Key, StructureMath.Number(TrendAlignment), StructureMath.Number(DistanceToInvalidationAtr),
        NetR is null ? "null" : StructureMath.Price(NetR.Value), StructureMath.Number(ExpectedNetR),
        StructureMath.Number(RelativeVolume), StructureMath.Number(VwapDistanceAtr),
        ZoneDistance is null ? "null" : StructureMath.Price(ZoneDistance.Value), TimeOfDay,
        CostComplete ? "1" : "0", ExecutionReady ? "1" : "0",
        string.Join(',', GateReasons), string.Join(',', FeatureContributions),
        Forecast is null ? "null" : string.Join(':', Forecast.Status, Forecast.ModelVersion,
            StructureMath.Number(Forecast.SuccessProbability), StructureMath.Number(Forecast.ExpectedValuePercent)),
        RegimeAssessment is null ? "null" : string.Join(':', RegimeAssessment.Status,
            RegimeAssessment.Regime?.Key ?? "null", StructureMath.Number(RegimeAssessment.Confidence)));
}
