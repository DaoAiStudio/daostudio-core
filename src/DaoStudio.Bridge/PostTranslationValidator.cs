using System.Text.RegularExpressions;

namespace DaoStudio.Bridge;

public sealed class PostTranslationValidator
{
    private static readonly Regex Tokens = new(@"\{\{(?:VAR|TAG|NUM)_[0-9]+\}\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public bool Validate(string maskedTemplate, string translatedTemplate) => Validate(maskedTemplate, translatedTemplate, out _);
    public bool Validate(string maskedTemplate, string translatedTemplate, out string? error)
    {
        var expected = Count(maskedTemplate);
        var actual = Count(translatedTemplate);
        bool valid = expected.Count == actual.Count && expected.All(p => actual.GetValueOrDefault(p.Key) == p.Value);
        error = valid ? null : "Placeholder multiplicity mismatch.";
        return valid;
    }
    private static Dictionary<string, int> Count(string text) => Tokens.Matches(text).Cast<Match>().GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count());
}
