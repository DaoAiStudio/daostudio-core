using System.Text.Json;
using DaoStudio.Contracts;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Engine;

public sealed class GlossaryMatcher : IGlossaryMatcher
{
    private readonly GlossaryEntry[] entries;
    public GlossaryMatcher(IReadOnlyList<GlossaryEntry> entries)
    {
        this.entries = entries.Select(e => e with { ForbiddenLiterals = e.ForbiddenLiterals.ToArray() }).ToArray();
        if (this.entries.Any(e => string.IsNullOrEmpty(e.Zh) || e.MaxChars < 0))
            throw new ArgumentException("Glossary requires nonempty source terms and nonnegative limits.", nameof(entries));
    }
    public static GlossaryMatcher FromJson(string json) => new(JsonSerializer.Deserialize<List<GlossaryEntry>>(json)
        ?? throw new JsonException("Expected glossary array."));
    public static GlossaryMatcher FromJsonFile(string path) => FromJson(File.ReadAllText(path, System.Text.Encoding.UTF8));
    public IReadOnlyList<GlossaryEntry> FindMatches(string sourceText) => entries
        .Where(e => sourceText.Contains(e.Zh, StringComparison.Ordinal)).ToArray();
    public GlossaryMatchResult ValidateTranslation(string sourceText, string translatedText, string targetLang = "pt-BR", int? maxCharsOverride = null)
    {
        if (maxCharsOverride < 0) throw new ArgumentOutOfRangeException(nameof(maxCharsOverride));
        var matches = FindMatches(sourceText);
        var forbidden = matches.SelectMany(e => e.ForbiddenLiterals)
            .Where(s => s.Length > 0 && translatedText.Contains(s, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // MaxChars is language-independent in the locked DTO; apply to the translated output.
        var limit = maxCharsOverride ?? matches.Select(e => e.MaxChars).Where(n => n.HasValue).Min();
        return new(matches, forbidden, limit.HasValue && translatedText.EnumerateRunes().Count() > limit.Value);
    }
}
