namespace Astra.Server.Domain.Structure;

/// <summary>
/// #132 §16A 종목 유형 게이트. `/stocks` 메타데이터로 레버리지 ETF·비보통주를 v5 신규 진입에서 제외한다.
/// 메타가 없으면 차단하지 않고 note만 남긴다(#93 tick UNKNOWN과 같은 원칙 — 모르면 허용).
/// </summary>
public static class SymbolEligibility
{
    /// <summary>정책이 허용하지 않는 종목 유형이다(신규 READY 금지).</summary>
    public const string CodeTypeUnsupported = "SYMBOL_TYPE_UNSUPPORTED";

    /// <summary>메타 조회 실패·결측이다. 신규 READY는 계속 허용한다.</summary>
    public const string NoteMetaUnknown = "SYMBOL_META_UNKNOWN";

    /// <summary>반환 null이 "허용 + 남길 사유 없음"이다.</summary>
    public static string? Note(StockInfo? info, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (info is null) return NoteMetaUnknown;
        var allowed = !policy.AllowedSecurityTypes.IsDefaultOrEmpty &&
                      policy.AllowedSecurityTypes.Contains(info.SecurityType, StringComparer.OrdinalIgnoreCase);
        if (!allowed || !info.IsCommonShare) return CodeTypeUnsupported;
        return info.LeverageFactor is { } factor && Math.Abs(factor) != 1m ? CodeTypeUnsupported : null;
    }

    public static bool IsDiagnostic(string code) =>
        string.Equals(code, CodeTypeUnsupported, StringComparison.Ordinal) ||
        string.Equals(code, NoteMetaUnknown, StringComparison.Ordinal);
}
