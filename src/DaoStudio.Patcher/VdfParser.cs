using System.Text;

namespace DaoStudio.Patcher;

public sealed record VdfNode(string? Value, IReadOnlyDictionary<string, VdfNode> Children)
{
    public string? GetValue(string key) => Children.TryGetValue(key, out var node) ? node.Value : null;
}

public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var offset = 0;
        string? Token()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset])) { offset++; continue; }
                if (text[offset] == '/' && offset + 1 < text.Length && text[offset + 1] == '/')
                { while (offset < text.Length && text[offset] != '\n') offset++; continue; }
                break;
            }
            if (offset == text.Length) return null;
            var c = text[offset++];
            if (c is '{' or '}') return c.ToString();
            var result = new StringBuilder();
            if (c == '"')
            {
                while (offset < text.Length)
                {
                    c = text[offset++];
                    if (c == '"') return result.ToString();
                    if (c == '\\' && offset < text.Length && text[offset] is '\\' or '"') c = text[offset++];
                    result.Append(c);
                }
                throw new InvalidDataException("Unterminated VDF string.");
            }
            result.Append(c);
            while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] is not '{' and not '}') result.Append(text[offset++]);
            return result.ToString();
        }
        VdfNode Object(int depth, bool nested)
        {
            if (depth > 64) throw new InvalidDataException("VDF nesting limit exceeded.");
            var children = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var key = Token();
                if (key == null) { if (nested) throw new InvalidDataException("Missing closing brace."); break; }
                if (key == "}") { if (!nested) throw new InvalidDataException("Unexpected closing brace."); break; }
                if (key == "{") throw new InvalidDataException("Expected VDF key.");
                var value = Token() ?? throw new InvalidDataException("Missing VDF value.");
                if (value == "}") throw new InvalidDataException("Missing VDF value.");
                if (!children.TryAdd(key, value == "{" ? Object(depth + 1, true) : new VdfNode(value, new Dictionary<string, VdfNode>())))
                    throw new InvalidDataException("Duplicate VDF key.");
            }
            return new VdfNode(null, children);
        }
        return Object(0, false);
    }
}
