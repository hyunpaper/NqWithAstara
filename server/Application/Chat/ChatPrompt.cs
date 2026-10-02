using System.Text;

namespace Astra.Server.Application.Chat;

public static class ChatPrompt
{
    public const string SystemKorean =
        "당신은 Astra 시장 모니터링 대시보드에 내장된 한국어 어시스턴트입니다. "
        + "항상 한국어로만 답합니다. 사용자가 다른 언어로 질문해도 한국어로 답합니다. "
        + "코드·식별자·티커는 원문 그대로 둡니다. 모르는 것은 모른다고 말하고, 투자 권유나 수익 보장을 하지 않습니다. "
        + "답은 간결하게, 필요하면 마크다운 목록·코드 블록을 씁니다.";

    public const string CheckUser = "연결 확인입니다. '네'라고만 답하세요.";

    /// <summary>최근 N턴만 남기고 글자 예산(컨텍스트 4096 보호) 안으로 자른다. 시스템 프롬프트는 항상 첫 번째다.</summary>
    public static IReadOnlyList<ChatMessage> Build(IReadOnlyList<ChatMessage> history, ChatOptions options)
    {
        var maxMessages = Math.Max(1, options.MaxTurns) * 2;
        var trimmed = history
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => new ChatMessage(m.Role.ToLowerInvariant(), Clip(m.Content.Trim(), Math.Max(100, options.MaxMessageChars))))
            .ToList();
        if (trimmed.Count > maxMessages) trimmed = trimmed.Skip(trimmed.Count - maxMessages).ToList();

        var budget = Math.Max(500, options.HistoryCharBudget);
        while (trimmed.Count > 1 && trimmed.Sum(m => m.Content.Length) > budget) trimmed.RemoveAt(0);

        var result = new List<ChatMessage>(trimmed.Count + 1) { new("system", SystemKorean) };
        result.AddRange(trimmed);
        return result;
    }

    static string Clip(string value, int limit)
    {
        if (value.Length <= limit) return value;
        var sb = new StringBuilder(limit + 3);
        sb.Append(value.AsSpan(0, limit)).Append('…');
        return sb.ToString();
    }
}
