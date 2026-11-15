using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DaoStudio.Contracts;
using DaoStudio.Engine;
using DaoStudio.Engine.Abstractions;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DaoStudio.Engine.Tests;

public sealed class MaskAndNormalizationTests
{
    private readonly TagMasker masker = new();
    [Theory]
    [InlineData("道友，你已突破炼气期，进入筑基期！")]
    [InlineData("<color=#fff>{PlayerName}</color>获得{0}灵石\\n道心坚定\n金丹期")]
    [InlineData("<size=24><b><i>鬼谷八荒</i></b></size><sprite=1>")]
    [InlineData("\r\n\n\r\\n")]
    [InlineData("{{VAR_0}} {PlayerName} {{TAG_0}}")]
    public void MaskRoundTrips(string source)
    {
        var result = masker.Mask(source);
        Assert.Equal(source, masker.Unmask(result.MaskedTemplate, result.Placeholders));
        Assert.True(masker.ValidateMaskIntegrity(result.MaskedTemplate, result.MaskedTemplate, result.Placeholders, out _));
    }
    [Fact] public void NestedTagsAreCanonical() => Assert.Equal("{{TAG_0}}{{VAR_0}}{{TAG_1}}", masker.Mask("<color=#fff>{PlayerName}</color>").MaskedTemplate);
    [Fact] public void RepeatedPlaceholderGetsUniqueTokens() => Assert.Equal("{{VAR_0}}{{VAR_1}}", masker.Mask("{0}{0}").MaskedTemplate);
    [Fact] public void NumbersAreOptional() => Assert.Equal("42", masker.Mask("42").MaskedTemplate);
    [Fact] public void NumbersAreMasked() => Assert.Equal("{{NUM_0}} + {{NUM_1}}", masker.Mask("42 + 3.5", true).MaskedTemplate);
    [Fact] public void EmbeddedNumbersStay() => Assert.Equal("abc42 42级", masker.Mask("abc42 42级", true).MaskedTemplate);
    [Theory]
    [InlineData("")]
    [InlineData("{{VAR_0}}{{VAR_0}}")]
    [InlineData("{{VAR_1}}")]
    [InlineData("{{var_0}}")]
    [InlineData("{{VAR_00}}")]
    public void CorruptTokenFails(string translated)
    {
        var r = masker.Mask("{PlayerName}");
        Assert.False(masker.ValidateMaskIntegrity(r.MaskedTemplate, translated, r.Placeholders, out var error));
        Assert.NotNull(error);
    }
    [Fact] public void UnexpectedTokenFails()
    {
        var r = masker.Mask("{0}");
        Assert.False(masker.ValidateMaskIntegrity(r.MaskedTemplate, r.MaskedTemplate + "{{NUM_0}}", r.Placeholders, out _));
    }
    [Fact] public void ReorderedTokensAreValid()
    {
        var r = masker.Mask("{0}{1}");
        Assert.True(masker.ValidateMaskIntegrity(r.MaskedTemplate, "{{VAR_1}}{{VAR_0}}", r.Placeholders, out var error));
        Assert.Null(error);
    }
    [Fact] public void UnknownUnmaskTokenRemains() => Assert.Equal("{{VAR_9}}", masker.Unmask("{{VAR_9}}", new Dictionary<string,string>()));
    [Fact] public void MalformedTagsHaveBoundedRuntime()
    {
        var input = new string('<', 200000) + new string('{', 200000);
        var watch = Stopwatch.StartNew();
        Assert.Equal(input, masker.Mask(input).MaskedTemplate);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
    [Fact] public void NormalizationComposesUnicode() => Assert.Equal("é 道心", new StringNormalizer().Normalize(" e\u0301\t  道心 "));
    [Fact] public void NormalizationPreservesLineBreaks() => Assert.Equal("甲\n乙\n丙", new StringNormalizer().Normalize("甲\r\n乙\r丙"));
    [Fact] public void NormalizationIsIdempotent()
    {
        var n = new StringNormalizer(); var s = n.Normalize("  鬼谷八荒\t 修仙\r\n炼气期 ");
        Assert.Equal(s, n.Normalize(s));
    }
    [Fact] public void HashMatchesKnownVector() => Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", new Sha256TemplateHasher().ComputeHash("abc"));
    [Fact] public void NormalizedHashesAreDeterministic()
    {
        var n = new StringNormalizer(); var h = new Sha256TemplateHasher();
        Assert.Equal(h.ComputeHash(n.Normalize(" 道心\t坚定 ")), h.ComputeHash(n.Normalize("道心 坚定")));
        Assert.Matches("^[a-f0-9]{64}$", h.ComputeHash("鬼谷八荒"));
    }
}

public sealed class CacheTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DaoStudioEngineTests-" + Guid.NewGuid());
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly ITestOutputHelper output;
    public CacheTests(ITestOutputHelper output) { this.output = output; Directory.CreateDirectory(directory); }
    private string Db => Path.Combine(directory, "cache.db");
    private static CacheKey K(string hash = "hash") => new("guigubahuang", "pt-BR", hash, 1);
    public void Dispose() => Directory.Delete(directory, true);
    [Fact] public async Task L0Miss() => Assert.False((await new L0MemoryCache().TryGetAsync(K())).Hit);
    [Fact] public async Task L0FifoAndOverwrite()
    {
        var c = new L0MemoryCache(2);
        await c.SetAsync(K("1"), "one"); await c.SetAsync(K("2"), "two");
        await c.SetAsync(K("1"), "updated"); Assert.Equal("updated", (await c.TryGetAsync(K("1"))).TranslatedTemplate);
        await c.SetAsync(K("3"), "three"); Assert.False((await c.TryGetAsync(K("1"))).Hit); Assert.Equal(2, c.Count);
    }
    [Fact] public async Task L0ConcurrentBound()
    {
        var c = new L0MemoryCache(10);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () => await c.SetAsync(K(i.ToString()), "道心"))));
        Assert.Equal(10, c.Count);
    }
    [Fact] public void L0RejectsZeroCapacity() => Assert.Throws<ArgumentOutOfRangeException>(() => new L0MemoryCache(0));
    [Fact] public async Task L1Miss() { using var c = new L1SqliteCache(Db, key); Assert.False((await c.TryGetAsync(K())).Hit); }
    [Fact] public async Task L1PersistsAcrossInstances()
    {
        using (var c = new L1SqliteCache(Db, key)) await c.SetAsync(K(), "Coração do Dao 道心");
        using var reopened = new L1SqliteCache(Db, key);
        Assert.Equal("Coração do Dao 道心", (await reopened.TryGetAsync(K())).TranslatedTemplate);
    }
    [Fact] public async Task L1DefaultSessionKeyWorks()
    {
        using (var c = new L1SqliteCache(Db)) await c.SetAsync(K(), "Dao");
        using var c2 = new L1SqliteCache(Db); Assert.True((await c2.TryGetAsync(K())).Hit);
    }
    [Fact] public async Task L1CompositeKeyIsolation()
    {
        using var c = new L1SqliteCache(Db, key); await c.SetAsync(K(), "original");
        foreach (var k in new[] { K() with { GameId = "other" }, K() with { TargetLang = "en" }, K() with { GlossaryVersion = 2 }, K("other") })
            Assert.False((await c.TryGetAsync(k)).Hit);
    }
    [Fact] public async Task L1Upsert() { using var c = new L1SqliteCache(Db, key); await c.SetAsync(K(), "a"); await c.SetAsync(K(), "b"); Assert.Equal("b", (await c.TryGetAsync(K())).TranslatedTemplate); }
    [Fact] public async Task L1ConcurrentWalConnections()
    {
        using var c1 = new L1SqliteCache(Db, key); using var c2 = new L1SqliteCache(Db, key);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(worker => Task.Run(async () =>
        {
            var c = worker % 2 == 0 ? c1 : c2;
            for (var i = 0; i < 40; i++)
            {
                var k = K($"{worker}-{i}"); await c.SetAsync(k, $"灵石 {i}");
                Assert.Equal($"灵石 {i}", (await c.TryGetAsync(k)).TranslatedTemplate);
            }
        })));
        using var db = new SqliteConnection($"Data Source={Db};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode"; Assert.Equal("wal", cmd.ExecuteScalar());
    }
    [Fact] public async Task L1WarmHitAverageBelowOneMillisecond()
    {
        using var c = new L1SqliteCache(Db, key); await c.SetAsync(K(), "Núcleo Dourado");
        for (var i = 0; i < 300; i++) await c.TryGetAsync(K());
        const int count = 3000; var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++) Assert.True((await c.TryGetAsync(K())).Hit);
        var average = watch.Elapsed.TotalMilliseconds / count;
        output.WriteLine($"L1 warm hit mean: {average:F6} ms ({count} hits)");
        Assert.True(average < 1, $"Mean hit latency {average:F6} ms");
    }
    [Fact] public async Task L1EncryptsDatabaseAndWal()
    {
        const string secret = "TRADUCAO_SECRETA_Coração_do_Dao_灵石";
        using (var c = new L1SqliteCache(Db, key))
        {
            await c.SetAsync(K(), secret);
            foreach (var file in Directory.GetFiles(directory))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                Assert.False(bytes.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0);
            }
        }
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(Db)));
    }
    [Fact] public async Task L1WrongKeyFailsAuthentication()
    {
        using (var c = new L1SqliteCache(Db, key)) await c.SetAsync(K(), "secret");
        using var wrong = new L1SqliteCache(Db, RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(async () => await wrong.TryGetAsync(K()));
    }
    [Fact] public void L1RejectsWrongKeySize() => Assert.Throws<ArgumentException>(() => new L1SqliteCache(Db, new byte[16]));
    [Fact] public async Task L1DisposalIsIdempotent()
    {
        var c = new L1SqliteCache(Db, key); c.Dispose(); await c.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await c.TryGetAsync(K()));
    }
    [Fact] public async Task CancellationIsHonored()
    {
        using var c = new L1SqliteCache(Db, key); var ct = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await c.SetAsync(K(), "x", ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await c.TryGetAsync(K(), ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new L0MemoryCache().TryGetAsync(K(), ct));
    }
    [Fact] public async Task TieredPromotesL1()
    {
        using var l1 = new L1SqliteCache(Db, key); await l1.SetAsync(K(), "Dao");
        var c = new TieredTranslationCache(new L0MemoryCache(), l1);
        Assert.Equal("L1", (await c.TryGetAsync(K())).Tier); Assert.Equal("L0", (await c.TryGetAsync(K())).Tier);
    }
    [Fact] public async Task TieredWritesBoth()
    {
        using var l1 = new L1SqliteCache(Db, key); var l0 = new L0MemoryCache(); var c = new TieredTranslationCache(l0, l1);
        await c.SetAsync(K(), "Dao"); Assert.Equal("Dao", (await l0.TryGetAsync(K())).TranslatedTemplate); Assert.Equal("Dao", (await l1.TryGetAsync(K())).TranslatedTemplate);
    }
}

public sealed class GlossaryAndParserTests
{
    private static GlossaryEntry Entry => new("炼气期", "Liàn Qì Qī", "Refinamento de Qi", "Qi Refining", "Refinamiento de Qi", "cultivation_realm", new[] { "Refinamento de Ar", "Gas Refining" }, 28);
    [Fact] public void FindsRealCjkTerm() => Assert.Single(new GlossaryMatcher(new[] { Entry }).FindMatches("你已突破炼气期！炼气期"));
    [Fact] public void NoSourceMatchNoRule() => Assert.Empty(new GlossaryMatcher(new[] { Entry }).ValidateTranslation("道心", "Gas Refining").ForbiddenHits);
    [Fact] public void ForbiddenIsCaseInsensitive() => Assert.Single(new GlossaryMatcher(new[] { Entry }).ValidateTranslation("炼气期", "GAS REFINING", "en").ForbiddenHits);
    [Fact] public void MaxCharsApplies() => Assert.True(new GlossaryMatcher(new[] { Entry }).ValidateTranslation("炼气期", new string('a', 29)).ExceedsMaxChars);
    [Fact] public void MaxCharsBoundary() => Assert.False(new GlossaryMatcher(new[] { Entry }).ValidateTranslation("炼气期", new string('a', 28)).ExceedsMaxChars);
    [Fact] public void MaxCharsOverride() => Assert.True(new GlossaryMatcher(new[] { Entry }).ValidateTranslation("炼气期", "Qi Refining", "en", 3).ExceedsMaxChars);
    [Fact] public void UnicodeCharactersCounted() => Assert.False(new GlossaryMatcher(new[] { Entry }).ValidateTranslation("炼气期", "😀", maxCharsOverride: 1).ExceedsMaxChars);
    [Fact] public void NoMatchHasNoLimit() => Assert.False(new GlossaryMatcher(new[] { Entry }).ValidateTranslation("道心", new string('x', 100)).ExceedsMaxChars);
    [Fact] public void RealGlossaryLoadsFromFile()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "data/glossary/guigubahuang-glossary.json"))) root = root.Parent;
        Assert.NotNull(root);
        var matcher = GlossaryMatcher.FromJsonFile(Path.Combine(root.FullName, "data/glossary/guigubahuang-glossary.json"));
        Assert.Single(matcher.FindMatches("炼气期")); Assert.Single(matcher.ValidateTranslation("炼气期", "Refinamento de Ar").ForbiddenHits);
    }
    [Fact] public void GlossaryJsonFactory() => Assert.Single(GlossaryMatcher.FromJson(System.Text.Json.JsonSerializer.Serialize(new[] { Entry })).FindMatches("炼气期"));
    [Fact] public void JsonFlatObject() => Assert.Equal("鬼谷八荒", new JsonLocalizationParser().Parse("{\"title\":\"鬼谷八荒\"}")[0].SourceText);
    [Fact] public void JsonRoundTripMetadata()
    {
        var p = new JsonLocalizationParser(); var s = new[] { new LocalizationSegment("1", "title", "鬼谷八荒", "Coração do Dao", true, "menu") };
        Assert.Equal(s, p.Parse(p.Export(s)));
    }
    [Fact] public void CsvQuotesCommasAndMultiline()
    {
        var p = new CsvLocalizationParser(); var s = p.Parse("key,text\r\na,\"道友,\"\"你好\"\"\r\n炼气期\"\r\n");
        Assert.Equal("道友,\"你好\"\r\n炼气期", s[0].SourceText); Assert.Equal(s, p.Parse(p.Export(s)));
    }
    [Fact] public void CsvRoundTripMetadata()
    {
        var p = new CsvLocalizationParser(); var s = new[] { new LocalizationSegment("1", "k", "道心", "", true, "Context,\"quoted\""), new LocalizationSegment("2", "k2", "甲") };
        Assert.Equal(s, p.Parse(p.Export(s)));
    }
    [Theory] [InlineData("k,\"unterminated")] [InlineData("k,\"a\"b")] [InlineData("k,a\"b")] [InlineData("k,a,b")]
    public void CsvMalformedFails(string text) => Assert.Throws<FormatException>(() => new CsvLocalizationParser().Parse(text));
    [Fact] public void CsvEmptyFields() => Assert.Equal("", new CsvLocalizationParser().Parse("key,text\nk,\n")[0].SourceText);
    [Fact] public void TextMixedRoundTrip()
    {
        var p = new TextLineParser(); var s = p.Parse("title=鬼谷八荒\n道友你好\n\nexpr=a=b\n=空键\n");
        Assert.Equal(s, p.Parse(p.Export(s)));
    }
    [Fact] public void TextExportsTranslation() => Assert.Equal("k=Coração do Dao\n", new TextLineParser().Export(new[] { new LocalizationSegment("1", "k", "道心", "Coração do Dao") }));
    [Fact] public void TextMultilineRejected() => Assert.Throws<FormatException>(() => new TextLineParser().Export(new[] { new LocalizationSegment("1", "k", "a\nb") }));
    [Fact] public void ParsersHandleUtf8Bom()
    {
        Assert.Single(new JsonLocalizationParser().Parse("\uFEFF{\"a\":\"道心\"}"));
        Assert.Single(new CsvLocalizationParser().Parse("\uFEFFkey,text\na,道心"));
        Assert.Equal("道心", new TextLineParser().Parse("\uFEFFa=道心")[0].SourceText);
    }
    [Fact] public void ExtensionsAreCaseInsensitive()
    {
        Assert.True(new JsonLocalizationParser().CanParse(".JSON")); Assert.True(new CsvLocalizationParser().CanParse(".CSV")); Assert.True(new TextLineParser().CanParse(".TXT"));
        Assert.False(new JsonLocalizationParser().CanParse(".csv"));
    }
    [Fact] public void EmptyParsersRoundTrip()
    {
        foreach (var p in new ILocalizationFileParser[] { new JsonLocalizationParser(), new CsvLocalizationParser(), new TextLineParser() })
            Assert.Empty(p.Parse(p.Export(Array.Empty<LocalizationSegment>())));
    }
}
