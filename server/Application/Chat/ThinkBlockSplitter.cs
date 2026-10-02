using System.Text;

namespace Astra.Server.Application.Chat;

/// <summary>스트림 조각에 섞여 오는 <c>&lt;think&gt;…&lt;/think&gt;</c>를 사고(thinking)와 본문으로 분리한다(#338). 조각 경계에 걸린 태그도 처리한다.</summary>
public sealed class ThinkBlockSplitter
{
    const string Open = "<think>";
    const string Close = "</think>";

    readonly StringBuilder _pending = new();
    bool _inThink;

    public (string Content, string Thinking) Push(string chunk)
    {
        _pending.Append(chunk);
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            var tag = _inThink ? Close : Open;
            var index = text.IndexOf(tag, StringComparison.Ordinal);
            if (index >= 0)
            {
                (_inThink ? thinking : content).Append(text, 0, index);
                _pending.Remove(0, index + tag.Length);
                _inThink = !_inThink;
                continue;
            }
            var keep = PartialTagLength(text, tag);
            (_inThink ? thinking : content).Append(text, 0, text.Length - keep);
            _pending.Remove(0, text.Length - keep);
            break;
        }
        return (content.ToString(), thinking.ToString());
    }

    public (string Content, string Thinking) Flush()
    {
        var rest = _pending.ToString();
        _pending.Clear();
        return _inThink ? ("", rest) : (rest, "");
    }

    static int PartialTagLength(string text, string tag)
    {
        var max = Math.Min(tag.Length - 1, text.Length);
        for (var len = max; len > 0; len--)
            if (text.AsSpan().EndsWith(tag.AsSpan(0, len), StringComparison.Ordinal)) return len;
        return 0;
    }
}
