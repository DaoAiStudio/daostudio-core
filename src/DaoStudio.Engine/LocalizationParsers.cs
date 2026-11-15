using System.Text;
using System.Text.Json;
using DaoStudio.Contracts;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Engine;

public sealed class JsonLocalizationParser : ILocalizationFileParser
{
    public string FormatName => "JSON";
    public bool CanParse(string fileExtension) => fileExtension.Equals(".json", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<LocalizationSegment> Parse(string content)
    {
        using var document = JsonDocument.Parse(content.TrimStart('\uFEFF'));
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<LocalizationSegment>>(document.RootElement.GetRawText())!;
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Expected object or segment array.");
        return document.RootElement.EnumerateObject().Select(p => new LocalizationSegment(p.Name, p.Name,
            p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new FormatException("Expected string values."))).ToArray();
    }
    // A segment array preserves all locked DTO fields, including review metadata.
    public string Export(IReadOnlyList<LocalizationSegment> segments) => JsonSerializer.Serialize(segments,
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

public sealed class CsvLocalizationParser : ILocalizationFileParser
{
    public string FormatName => "CSV";
    public bool CanParse(string fileExtension) => fileExtension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<LocalizationSegment> Parse(string content)
    {
        var rows = ReadRows(content.TrimStart('\uFEFF'));
        if (rows.Count == 0) return Array.Empty<LocalizationSegment>();
        var full = rows[0].SequenceEqual(new[] { "id", "key", "sourceText", "translatedText", "approved", "context" });
        var header = full || (rows[0].Count == 2 && rows[0][0].Equals("key", StringComparison.OrdinalIgnoreCase) &&
            (rows[0][1].Equals("text", StringComparison.OrdinalIgnoreCase) || rows[0][1].Equals("sourceText", StringComparison.OrdinalIgnoreCase)));
        return rows.Skip(header ? 1 : 0).Select((r, i) =>
        {
            if (r.Count != (full ? 6 : 2)) throw new FormatException("Inconsistent CSV column count.");
            return full ? new LocalizationSegment(r[0], r[1], r[2], DecodeNullable(r[3]), bool.Parse(r[4]), DecodeNullable(r[5]))
                : new LocalizationSegment(i.ToString(System.Globalization.CultureInfo.InvariantCulture), r[0], r[1]);
        }).ToArray();
    }
    // JSON encoding of nullable fields distinguishes null from empty strings without losing metadata.
    private static string? DecodeNullable(string value) => JsonSerializer.Deserialize<string?>(value);
    public string Export(IReadOnlyList<LocalizationSegment> segments)
    {
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var result = new StringBuilder("id,key,sourceText,translatedText,approved,context\r\n");
        foreach (var s in segments)
            result.AppendLine(string.Join(",", new[] { s.Id, s.Key, s.SourceText, JsonSerializer.Serialize(s.TranslatedText),
                s.Approved.ToString(), JsonSerializer.Serialize(s.Context) }.Select(Quote)));
        return result.ToString();
    }
    private static List<List<string>> ReadRows(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                else { quoted = false; closed = true; }
            }
            else if (c == ',' || c == '\r' || c == '\n')
            {
                row.Add(field.ToString()); field.Clear(); closed = false;
                if (c != ',')
                {
                    rows.Add(row); row = new();
                    if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                }
            }
            else if (closed) throw new FormatException("Unexpected text after closing quote.");
            else if (c == '"')
            {
                if (field.Length != 0) throw new FormatException("Quote inside unquoted field.");
                quoted = true;
            }
            else field.Append(c);
        }
        if (quoted) throw new FormatException("Unterminated CSV quote.");
        if (row.Count > 0 || field.Length > 0 || closed) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}

public sealed class TextLineParser : ILocalizationFileParser
{
    public string FormatName => "Text";
    public bool CanParse(string fileExtension) => fileExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<LocalizationSegment> Parse(string content)
    {
        var result = new List<LocalizationSegment>();
        using var reader = new StringReader(content.TrimStart('\uFEFF'));
        while (reader.ReadLine() is { } line)
        {
            var split = line.IndexOf('=');
            var id = result.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result.Add(new(id, split < 0 ? "" : line[..split], split < 0 ? line : line[(split + 1)..],
                Context: split < 0 ? "text:plain" : "text:keyed"));
        }
        return result;
    }
    public string Export(IReadOnlyList<LocalizationSegment> segments)
    {
        var result = new StringBuilder();
        foreach (var s in segments)
        {
            var text = s.TranslatedText ?? s.SourceText;
            if (text.Contains('\n') || text.Contains('\r') || s.Key.IndexOfAny(['\r', '\n', '=']) >= 0)
                throw new FormatException("Line format cannot represent multiline text or keys containing '='.");
            var plain = s.Context == "text:plain" || (s.Context != "text:keyed" && s.Key.Length == 0);
            if (plain && text.Contains('=')) throw new FormatException("Plain lines cannot contain '='.");
            result.Append(plain ? text : s.Key + "=" + text).Append('\n');
        }
        return result.ToString();
    }
}
