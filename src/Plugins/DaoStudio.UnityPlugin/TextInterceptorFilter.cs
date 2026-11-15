using System.Text;
using DaoStudio.Contracts;

namespace DaoStudio.UnityPlugin;

public static class TextInterceptorFilter
{
    public static bool ShouldTranslate(string? text) => !string.IsNullOrWhiteSpace(text) && text.EnumerateRunes().Any(r =>
        r.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or
        >= 0x20000 and <= 0x323AF or >= 0x3041 and <= 0x3096 or >= 0x30A1 and <= 0x30FA or
        >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9D or >= 0x1100 and <= 0x11FF or
        >= 0x3131 and <= 0x318E or >= 0xAC00 and <= 0xD7A3 or >= 0xA960 and <= 0xA97F or >= 0xD7B0 and <= 0xD7FF);

    public static UiHint InferUiHint(string text, string? context = null)
    {
        var name = context?.ToLowerInvariant() ?? "";
        if (name.Contains("button") || name.Contains("btn")) return UiHint.Button;
        if (name.Contains("tooltip") || name.Contains("description")) return UiHint.Tooltip;
        if (name.Contains("item") || name.Contains("inventory")) return UiHint.ItemName;
        if (name.Contains("dialog") || name.Contains("chat")) return UiHint.Dialogue;
        if (text.Contains('\n')) return UiHint.Tooltip;
        if (text.Length > 20 || text.IndexOfAny(new[] { '。', '！', '？', '「', '」' }) >= 0) return UiHint.Dialogue;
        return text.Length <= 4 ? UiHint.Button : UiHint.ItemName;
    }

    public static bool TryFilter(string? text, out UiHint hint, string? context = null)
    {
        hint = ShouldTranslate(text) ? InferUiHint(text!, context) : UiHint.Unknown;
        return hint != UiHint.Unknown;
    }
}
