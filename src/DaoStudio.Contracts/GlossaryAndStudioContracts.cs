using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DaoStudio.Contracts
{
    public sealed record GlossaryEntry(
        [property: JsonPropertyName("zh")] string Zh,
        [property: JsonPropertyName("pinyin")] string Pinyin,
        [property: JsonPropertyName("ptBr")] string PtBr,
        [property: JsonPropertyName("en")] string En,
        [property: JsonPropertyName("es")] string Es,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("forbiddenLiterals")] IReadOnlyList<string> ForbiddenLiterals,
        [property: JsonPropertyName("maxChars")] int? MaxChars = null);

    public sealed record LocalizationSegment(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("sourceText")] string SourceText,
        [property: JsonPropertyName("translatedText")] string? TranslatedText = null,
        [property: JsonPropertyName("approved")] bool Approved = false,
        [property: JsonPropertyName("context")] string? Context = null);
}
