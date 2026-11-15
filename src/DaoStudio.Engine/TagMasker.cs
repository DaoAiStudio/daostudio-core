using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Engine;

public sealed class TagMasker : ITagMasker
{
    private static readonly Regex Parts = new(@"\{\{(?:VAR|TAG|NUM)_[0-9]+\}\}|\{[\p{L}\p{N}_]+\}|</?(?:color|size|sprite|b|i)(?:=[^<>\r\n]*)?\s*/?>|\\n|\r\n|\n|\r|\b[0-9]+(?:[.,][0-9]+)?\b",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Tokens = new(@"\{\{(?:VAR|TAG|NUM)_[0-9]+\}\}", RegexOptions.NonBacktracking);

    public MaskedTextResult Mask(string input, bool maskStandaloneNumbers = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var counters = new Dictionary<string, int> { ["VAR"] = 0, ["TAG"] = 0, ["NUM"] = 0 };
        var template = Parts.Replace(input, m =>
        {
            var kind = m.Value[0] == '{' ? "VAR" : char.IsDigit(m.Value[0]) ? "NUM" : "TAG";
            if (kind == "NUM" && !maskStandaloneNumbers) return m.Value;
            var token = "{{" + kind + "_" + counters[kind]++ + "}}";
            values.Add(token, m.Value);
            return token;
        });
        return new(template, new ReadOnlyDictionary<string, string>(values));
    }

    // One pass prevents restored source text resembling a token from being expanded again.
    public string Unmask(string translatedTemplate, IReadOnlyDictionary<string, string> placeholders) =>
        Tokens.Replace(translatedTemplate, m => placeholders.TryGetValue(m.Value, out var original) ? original : m.Value);

    public bool ValidateMaskIntegrity(string maskedTemplate, string translatedTemplate,
        IReadOnlyDictionary<string, string> placeholders, out string? errorReason)
    {
        static Dictionary<string, int> Count(string text) => Tokens.Matches(text).Cast<Match>()
            .GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var source = Count(maskedTemplate);
        var target = Count(translatedTemplate);
        if (source.Count != placeholders.Count || source.Any(p => p.Value != 1 || !placeholders.ContainsKey(p.Key)) ||
            target.Count != source.Count || source.Any(p => !target.TryGetValue(p.Key, out var count) || count != p.Value))
        {
            errorReason = "Missing, duplicated, altered or unexpected mask token.";
            return false;
        }
        errorReason = null;
        return true;
    }
}
