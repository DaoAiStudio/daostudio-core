using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Engine;

public sealed class StringNormalizer : IStringNormalizer
{
    private static readonly Regex Spaces = new(@"[^\S\r\n]+", RegexOptions.NonBacktracking);
    // NFC preserves CJK compatibility distinctions; line breaks remain semantically significant.
    public string Normalize(string input) => Spaces.Replace(input.Normalize(NormalizationForm.FormC)
        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), " ").Trim();
}

public sealed class Sha256TemplateHasher : ITemplateHashCalculator
{
    public string ComputeHash(string normalizedTemplate) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTemplate))).ToLowerInvariant();
}
