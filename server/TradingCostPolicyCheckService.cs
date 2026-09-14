using System.Globalization;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Structure;

namespace Astra.Server;

/// <summary>기동 시 거래 비용 기본값과 설정 바인딩 결과의 불일치를 경고한다.</summary>
public sealed class TradingCostPolicyCheckService
{
    const double MismatchTolerance = .0001;

    public TradingCostPolicyCheckService(StructurePolicy structurePolicy, ILogger<TradingCostPolicyCheckService> logger)
    {
        WarnIfMismatch("StructurePolicy.RoundTripFeePercent", structurePolicy.RoundTripFeePercent, logger);
        WarnIfMismatch("MeasurementPolicy.RoundTripFeePercent", MeasurementPolicy.Default.RoundTripFeePercent, logger);
        WarnIfMismatch("MarketRules.RoundTripFeePercent", MarketRules.RoundTripFeePercent, logger);
    }

    static void WarnIfMismatch(string source, double value, ILogger logger)
    {
        if (Math.Abs(value - TradingCostDefaults.RoundTripFeePercent) <= MismatchTolerance) return;

        logger.LogWarning("TRADING_COST_DEFAULT_MISMATCH:{Source}=value:{Value};default:{Default}",
            source,
            value.ToString(CultureInfo.InvariantCulture),
            TradingCostDefaults.RoundTripFeePercent.ToString(CultureInfo.InvariantCulture));
    }
}
