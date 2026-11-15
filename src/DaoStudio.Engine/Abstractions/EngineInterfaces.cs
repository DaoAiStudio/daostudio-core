using DaoStudio.Contracts;

namespace DaoStudio.Engine.Abstractions;

public sealed record MaskedTextResult(
    string MaskedTemplate,
    IReadOnlyDictionary<string, string> Placeholders);

public interface ITagMasker
{
    MaskedTextResult Mask(string input, bool maskStandaloneNumbers = false);
    string Unmask(string translatedTemplate, IReadOnlyDictionary<string, string> placeholders);
    bool ValidateMaskIntegrity(
        string maskedTemplate,
        string translatedTemplate,
        IReadOnlyDictionary<string, string> placeholders,
        out string? errorReason);
}

public interface IStringNormalizer
{
    string Normalize(string input);
}

public interface ITemplateHashCalculator
{
    string ComputeHash(string normalizedTemplate);
}

public sealed record CacheKey(
    string GameId,
    string TargetLang,
    string TemplateHash,
    int GlossaryVersion);

public sealed record CacheLookupResult(
    bool Hit,
    string? TranslatedTemplate,
    string Tier);

public interface ITranslationCache
{
    ValueTask<CacheLookupResult> TryGetAsync(CacheKey key, CancellationToken ct = default);
    ValueTask SetAsync(CacheKey key, string translatedTemplate, CancellationToken ct = default);
}

public sealed record GlossaryMatchResult(
    IReadOnlyList<GlossaryEntry> MatchedEntries,
    IReadOnlyList<string> ForbiddenHits,
    bool ExceedsMaxChars);

public interface IGlossaryMatcher
{
    IReadOnlyList<GlossaryEntry> FindMatches(string sourceText);
    GlossaryMatchResult ValidateTranslation(
        string sourceText,
        string translatedText,
        string targetLang = "pt-BR",
        int? maxCharsOverride = null);
}

public interface ILocalizationFileParser
{
    string FormatName { get; }
    bool CanParse(string fileExtension);
    IReadOnlyList<LocalizationSegment> Parse(string content);
    string Export(IReadOnlyList<LocalizationSegment> segments);
}
