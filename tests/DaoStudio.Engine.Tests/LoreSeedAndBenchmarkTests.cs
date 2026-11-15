using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DaoStudio.Contracts;
using DaoStudio.Engine;
using DaoStudio.Engine.Abstractions;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DaoStudio.Engine.Tests;

public sealed class LoreSeedAndBenchmarkTests(ITestOutputHelper output)
{
    private static string DataPath(string relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "data", relative)))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "data", relative);
    }

    private static T[] Read<T>(string path) =>
        JsonSerializer.Deserialize<T[]>(File.ReadAllText(DataPath(path)))!;
    private static GlossaryEntry[] Glossary() => Read<GlossaryEntry>("glossary/guigubahuang-glossary.json");
    private static LocalizationSegment[] Seed() => Read<LocalizationSegment>("seed/guigubahuang-seed-samples.json");
    private static CacheKey Key(string template) => new("guigubahuang", "pt-BR",
        new Sha256TemplateHasher().ComputeHash(new StringNormalizer().Normalize(template)), 1);

    [Fact]
    public void GlossaryHasCompleteUniqueCanonicalEntries()
    {
        var entries = Glossary();
        Assert.True(entries.Length >= 55);
        Assert.Equal(entries.Length, entries.Select(e => e.Zh).Distinct(StringComparer.Ordinal).Count());
        foreach (var e in entries)
        {
            foreach (var field in new[] { e.Zh, e.Pinyin, e.PtBr, e.En, e.Es, e.Category })
                Assert.False(string.IsNullOrWhiteSpace(field), $"Incomplete entry: {e.Zh}");
            Assert.NotNull(e.ForbiddenLiterals);
            Assert.True(e.MaxChars > 0);
        }
        Assert.Single(new GlossaryMatcher(entries).FindMatches("炼气期"));
        output.WriteLine($"Canonical glossary: {entries.Length} unique complete entries.");
    }

    [Fact]
    public void ApprovedSeedRespectsGlossaryAndPreservesEveryPlaceholder()
    {
        var seed = Seed();
        var entries = Glossary();
        var matcher = new GlossaryMatcher(entries);
        var masker = new TagMasker();
        Assert.True(seed.Length >= 40);
        Assert.Equal(seed.Length, seed.Select(s => s.Id).Distinct().Count());
        Assert.Equal(seed.Length, seed.Select(s => s.Key).Distinct().Count());
        var contexts = new[] { "button", "dialogue", "tooltip", "item_name", "system_message" };
        foreach (var segment in seed)
        {
            Assert.True(segment.Approved, segment.Id);
            Assert.False(string.IsNullOrWhiteSpace(segment.Id));
            Assert.False(string.IsNullOrWhiteSpace(segment.Key));
            Assert.False(string.IsNullOrWhiteSpace(segment.SourceText));
            Assert.False(string.IsNullOrWhiteSpace(segment.TranslatedText));
            Assert.Contains(segment.Context, contexts);
            Assert.Empty(matcher.ValidateTranslation(segment.SourceText, segment.TranslatedText!).ForbiddenHits);
            // Also catch forbidden literals belonging to a term absent from the source.
            foreach (var literal in entries.SelectMany(e => e.ForbiddenLiterals).Where(s => s.Length > 0))
                Assert.False(segment.TranslatedText!.Contains(literal, StringComparison.OrdinalIgnoreCase), segment.Id);
            foreach (var entry in matcher.FindMatches(segment.SourceText))
            {
                // Preserve the original seed's grammatical plural, without changing its wording.
                var plural = entry.Zh == "灵石" && segment.TranslatedText!.Contains("Pedras Espirituais", StringComparison.Ordinal);
                Assert.True(plural || segment.TranslatedText!.Contains(entry.PtBr, StringComparison.Ordinal),
                    $"{segment.Id}: missing canonical translation {entry.PtBr}");
            }
            var source = masker.Mask(segment.SourceText);
            var target = masker.Mask(segment.TranslatedText!);
            Assert.True(masker.ValidateMaskIntegrity(source.MaskedTemplate, target.MaskedTemplate,
                source.Placeholders, out var error), $"{segment.Id}: {error}");
            // Integrity alone checks token multiplicity, not original tag/variable values.
            Assert.Equal(source.Placeholders.OrderBy(p => p.Key), target.Placeholders.OrderBy(p => p.Key));
            Assert.Equal(segment.SourceText, masker.Unmask(source.MaskedTemplate, source.Placeholders));
            Assert.Equal(segment.TranslatedText, masker.Unmask(target.MaskedTemplate, source.Placeholders));
            if (source.Placeholders.Count > 0)
                Assert.False(masker.ValidateMaskIntegrity(source.MaskedTemplate,
                    target.MaskedTemplate.Replace(source.Placeholders.Keys.First(), "", StringComparison.Ordinal),
                    source.Placeholders, out _));
        }
        Assert.Contains(seed, s => s.SourceText.Contains("{PlayerName}"));
        Assert.Contains(seed, s => s.SourceText.Contains("{0}"));
        Assert.Contains(seed, s => s.SourceText.Contains("<color=#"));
        Assert.Contains(seed, s => s.SourceText.Contains("\\n"));
        Assert.Contains(seed, s => s.SourceText.Contains('\n'));
        output.WriteLine($"Approved seed: {seed.Length}; forbidden hits: 0; structural integrity: 100%.");
    }

    [Fact]
    public async Task SeedAndInitialGameplayExceedEightyPercentHitsBelowOneMillisecond()
    {
        var seed = Seed();
        Assert.True(seed.Length >= 40);
        var masker = new TagMasker();
        var directory = Path.Combine(Path.GetTempPath(), "DaoStudioLoreBenchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "seed.db");
            var encryptionKey = RandomNumberGenerator.GetBytes(32);
            using (var writer = new L1SqliteCache(database, encryptionKey))
            {
                var cache = new TieredTranslationCache(new L0MemoryCache(seed.Length), writer);
                foreach (var segment in seed)
                    await cache.SetAsync(Key(masker.Mask(segment.SourceText).MaskedTemplate),
                        masker.Mask(segment.TranslatedText!).MaskedTemplate);
            }
            using var l1 = new L1SqliteCache(database, encryptionKey);
            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode";
                Assert.Equal("wal", command.ExecuteScalar());
            }
            var tiered = new TieredTranslationCache(new L0MemoryCache(seed.Length), l1);
            // Reopen persisted encrypted seed and prove L1 -> L0 promotion for every entry.
            foreach (var segment in seed)
            {
                var key = Key(masker.Mask(segment.SourceText).MaskedTemplate);
                var persisted = await tiered.TryGetAsync(key);
                Assert.True(persisted.Hit);
                Assert.Equal("L1", persisted.Tier);
                Assert.Equal(masker.Mask(segment.TranslatedText!).MaskedTemplate, persisted.TranslatedTemplate);
                Assert.Equal("L0", (await tiered.TryGetAsync(key)).Tier);
            }
            // JIT/warm-up is separate from the 100-query measured scenario.
            for (var i = 0; i < 300; i++)
                await tiered.TryGetAsync(Key(masker.Mask(seed[i % seed.Length].SourceText).MaskedTemplate));

            var dynamicSeed = seed.Where(s => s.SourceText.Contains("{PlayerName}") && s.SourceText.Contains("{0}")).ToArray();
            Assert.NotEmpty(dynamicSeed);
            var hits = 0;
            var misses = 0;
            long warmTicks = 0;
            var names = new HashSet<string>();
            var numbers = new HashSet<string>();
            for (var i = 0; i < 100; i++)
            {
                // Each block has 17 seeded queries and 3 novel misses, interleaved over five blocks.
                var expectedHit = i % 20 < 17;
                var ordinal = (i / 20) * 17 + i % 20;
                var segment = expectedHit
                    ? (ordinal < seed.Length ? seed[ordinal] : dynamicSeed[ordinal % dynamicSeed.Length])
                    : new LocalizationSegment($"miss-{i}", $"new.{i}", $"未收录的遭遇：陌生旅人{i}。", null);
                var source = masker.Mask(segment.SourceText);
                var key = Key(source.MaskedTemplate);
                var start = Stopwatch.GetTimestamp();
                var result = await tiered.TryGetAsync(key);
                var elapsed = Stopwatch.GetTimestamp() - start;
                Assert.Equal(expectedHit, result.Hit);
                if (!result.Hit) { misses++; continue; }
                hits++;
                warmTicks += elapsed;
                Assert.Equal("L0", result.Tier);
                Assert.True(masker.ValidateMaskIntegrity(source.MaskedTemplate, result.TranslatedTemplate!, source.Placeholders, out _));
                // Runtime bindings are supplied separately, before rendering; arbitrary names in raw text
                // cannot be inferred by TagMasker. Never put a previous player's rendered text in cache.
                var player = $"Cultivador-{i}";
                var amount = (100 + i * 7).ToString(CultureInfo.InvariantCulture);
                var bindings = source.Placeholders.ToDictionary(p => p.Key, p => p.Value switch
                {
                    "{PlayerName}" => player,
                    "{0}" => amount,
                    _ => p.Value
                });
                var rendered = masker.Unmask(result.TranslatedTemplate!, bindings);
                var expected = segment.TranslatedText!.Replace("{PlayerName}", player).Replace("{0}", amount);
                Assert.Equal(expected, rendered);
                if (segment.SourceText.Contains("{PlayerName}")) names.Add(player);
                if (segment.SourceText.Contains("{0}")) numbers.Add(amount);
            }
            Assert.Equal(85, hits);
            Assert.Equal(15, misses);
            Assert.True(names.Count >= 10 && numbers.Count >= 10);
            var hitRate = hits / 100.0;
            var meanMs = warmTicks * 1000.0 / Stopwatch.Frequency / hits;
            output.WriteLine($"Seed={seed.Length}; queries=100; hits={hits}; misses={misses}; hit rate={hitRate:P2}; warm lookup mean={meanMs:F6} ms; names={names.Count}; numbers={numbers.Count}.");
            Assert.True(hitRate > 0.80);
            Assert.True(meanMs < 1, $"Mean warm lookup: {meanMs:F6} ms");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
