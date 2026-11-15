using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DaoStudio.Contracts
{
    public enum UiHint
    {
        Unknown = 0,
        Dialogue = 1,
        Button = 2,
        Tooltip = 3,
        ItemName = 4,
        SystemMessage = 5
    }

    public sealed record TranslationRequest(
        [property: JsonPropertyName("game_id")] string GameId,
        [property: JsonPropertyName("source_text")] string SourceText,
        [property: JsonPropertyName("target_lang")] string TargetLang = "pt-BR",
        [property: JsonPropertyName("glossary_version")] int GlossaryVersion = 1,
        [property: JsonPropertyName("ui_hint")] UiHint UiHint = UiHint.Unknown,
        [property: JsonPropertyName("max_chars")] int? MaxChars = null);

    public sealed record TranslationItem(
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("template")] string Template,
        [property: JsonPropertyName("ui_hint")] string UiHint = "dialogue",
        [property: JsonPropertyName("max_chars")] int? MaxChars = null);

    public sealed record TranslationBatchRequest(
        [property: JsonPropertyName("game_id")] string GameId,
        [property: JsonPropertyName("target_lang")] string TargetLang,
        [property: JsonPropertyName("glossary_version")] int GlossaryVersion,
        [property: JsonPropertyName("items")] IReadOnlyList<TranslationItem> Items);

    public sealed record TranslationResultItem(
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("translated_template")] string TranslatedTemplate,
        [property: JsonPropertyName("from_cache")] bool FromCache = false);

    public sealed record TranslationBatchResponse(
        [property: JsonPropertyName("game_id")] string GameId,
        [property: JsonPropertyName("target_lang")] string TargetLang,
        [property: JsonPropertyName("items")] IReadOnlyList<TranslationResultItem> Items);

    public sealed record BridgeTranslateRequest(
        [property: JsonPropertyName("game_id")] string GameId,
        [property: JsonPropertyName("source_text")] string SourceText,
        [property: JsonPropertyName("target_lang")] string TargetLang = "pt-BR",
        [property: JsonPropertyName("glossary_version")] int GlossaryVersion = 1,
        [property: JsonPropertyName("ui_hint")] string UiHint = "dialogue",
        [property: JsonPropertyName("max_chars")] int? MaxChars = null);

    public sealed record BridgeTranslateResponse(
        [property: JsonPropertyName("source_text")] string SourceText,
        [property: JsonPropertyName("translated_text")] string TranslatedText,
        [property: JsonPropertyName("template_hash")] string TemplateHash,
        [property: JsonPropertyName("cache_tier")] string CacheTier,
        [property: JsonPropertyName("fallback_used")] bool FallbackUsed,
        [property: JsonPropertyName("error")] string? Error = null);
}
