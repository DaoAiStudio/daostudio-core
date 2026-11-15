using System.Text.Json;
using DaoStudio.Contracts;
using DaoStudio.Engine;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Desktop.Services;

public sealed class DesktopResources
{
    private static readonly GameProfileManifest[] BuiltInFallbacks =
    {
        new("guigubahuang", "Tale of Immortal (鬼谷八荒)", "1468810", "guigubahuang.exe", "unity-il2cpp",
            new("melonloader", "0.6.x", "https://github.com/LavaGang/MelonLoader/releases/download/v0.6.6/MelonLoader.x64.zip", ""),
            new("NotoSansCJK_SC_SDF", "SIL-OFL-1.1", "https://cdn.daostudio.ai/fonts/notosans_cjk_sc_sdf.bundle", ""), "1.0.0", 1),
        new("michangsheng", "Mi ChangSheng (觅长生)", "1189490", "觅长生.exe", "unity-mono",
            new("bepinex", "5.4.x", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip", ""),
            new("NotoSansCJK_SC_SDF", "SIL-OFL-1.1", "https://cdn.daostudio.ai/fonts/notosans_cjk_sc_sdf.bundle", ""), "1.0.0", 1),
        new("xiuxiansimulator", "Amazing Cultivation Simulator (了不起的修仙模拟器)", "955900", "Amazing Cultivation Simulator.exe", "unity-mono",
            new("bepinex", "5.4.x", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip", ""),
            new("NotoSansCJK_SC_SDF", "SIL-OFL-1.1", "https://cdn.daostudio.ai/fonts/notosans_cjk_sc_sdf.bundle", ""), "1.0.0", 1),
        new("herosadventure", "Hero's Adventure: Road to Passion (大侠立志传)", "1948980", "Hero's Adventure.exe", "unity-il2cpp",
            new("bepinex", "6.0.x", "https://github.com/BepInEx/BepInEx/releases", ""),
            new("NotoSansCJK_SC_SDF", "SIL-OFL-1.1", "https://cdn.daostudio.ai/fonts/notosans_cjk_sc_sdf.bundle", ""), "1.0.0", 1),
        new("wanderingsword", "Wandering Sword (逸剑风云决)", "1876890", "Wandering Sword.exe", "unity-mono",
            new("bepinex", "5.4.x", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip", ""),
            new("NotoSansCJK_SC_SDF", "SIL-OFL-1.1", "https://cdn.daostudio.ai/fonts/notosans_cjk_sc_sdf.bundle", ""), "1.0.0", 1)
    };

    public string Root { get; }
    public DesktopResources(string root) => Root = root;
    public string ProfileWarning { get; private set; } = "";

    public GameProfileManifest LoadProfile()
    {
        try
        {
            var profile = JsonSerializer.Deserialize<GameProfileManifest>(File.ReadAllText(Path.Combine(Root, "profiles", "guigubahuang.json")));
            if (profile is null || profile.GameId != "guigubahuang" || profile.SteamAppId != "1468810" ||
                profile.ExecutableName != "guigubahuang.exe" || profile.GlossaryVersion < 1) throw new InvalidDataException("Perfil inválido.");
            return profile;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ProfileWarning = "Perfil indisponível; usando identificação segura de Tale of Immortal (fallback).";
            return BuiltInFallbacks[0];
        }
    }

    public IReadOnlyList<GameProfileManifest> LoadAllProfiles()
    {
        var byId = new Dictionary<string, GameProfileManifest>(StringComparer.OrdinalIgnoreCase);
        var defaultProfile = LoadProfile();
        byId[defaultProfile.GameId] = defaultProfile;

        var profilesDir = Path.Combine(Root, "profiles");
        if (Directory.Exists(profilesDir))
        {
            foreach (var file in Directory.EnumerateFiles(profilesDir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<GameProfileManifest>(File.ReadAllText(file));
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.GameId) ||
                        string.IsNullOrWhiteSpace(manifest.SteamAppId) ||
                        string.IsNullOrWhiteSpace(manifest.ExecutableName) ||
                        manifest.GlossaryVersion < 1)
                    {
                        continue;
                    }

                    byId.TryAdd(manifest.GameId, manifest);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // Skip malformed custom profile without blocking startup.
                }
            }
        }

        // Ensure all 5 canonical games remain selectable even in sparse/temp environments.
        foreach (var fallback in BuiltInFallbacks)
            byId.TryAdd(fallback.GameId, fallback);

        var ordered = new List<GameProfileManifest>(byId.Count) { byId["guigubahuang"] };
        foreach (var fallback in BuiltInFallbacks.Skip(1))
        {
            if (byId.TryGetValue(fallback.GameId, out var manifest))
                ordered.Add(manifest);
        }

        foreach (var extra in byId.Values
            .Where(m => BuiltInFallbacks.All(b => !string.Equals(b.GameId, m.GameId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            ordered.Add(extra);
        }

        return ordered;
    }

    public string SeedPath => GetSeedPath("guigubahuang");

    public string GetSeedPath(string? gameId)
    {
        var safeId = string.IsNullOrWhiteSpace(gameId) ? "guigubahuang" : gameId.Trim().ToLowerInvariant();
        var candidate = Path.Combine(Root, "data", "seed", $"{safeId}-seed-samples.json");
        return File.Exists(candidate) ? candidate : Path.Combine(Root, "data", "seed", "guigubahuang-seed-samples.json");
    }

    public string GetGlossaryPath(string? gameId)
    {
        var safeId = string.IsNullOrWhiteSpace(gameId) ? "guigubahuang" : gameId.Trim().ToLowerInvariant();
        var candidate = Path.Combine(Root, "data", "glossary", $"{safeId}-glossary.json");
        return File.Exists(candidate) ? candidate : Path.Combine(Root, "data", "glossary", "guigubahuang-glossary.json");
    }

    public IGlossaryMatcher LoadGlossary(string? gameId, IGlossaryMatcher fallback)
    {
        var path = GetGlossaryPath(gameId);
        return File.Exists(path) ? GlossaryMatcher.FromJsonFile(path) : fallback;
    }

    public IReadOnlyList<MaterialBlockItem> LoadMaterialSamples()
    {
        var path = Path.Combine(Root, "data", "materials", "xianxia-materials-samples.json");
        if (File.Exists(path))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<MaterialBlockItem>>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (items is { Count: > 0 }) return items;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Fall back to embedded defaults below.
            }
        }

        return new[]
        {
            new MaterialBlockItem("mh-001", "Manhua", "Pág. 01 • Painel 01", "Narração", "大荒历三千年，灵气复苏。", "Ano 3.000 do Grande Ermo: o Qi Espiritual desperta.", 65),
            new MaterialBlockItem("mh-002", "Manhua", "Pág. 01 • Painel 02", "Diálogo", "{PlayerName}，你终于踏入筑基期了！", "{PlayerName}, enfim alcançou o Estabelecimento de Fundação!", 64),
            new MaterialBlockItem("mh-004", "Manhua", "Pág. 02 • Painel 03", "SFX", "轰隆——剑鸣！", "ZUUUM — ECO DA ESPADA!", 28),
            new MaterialBlockItem("wn-001", "Webnovel", "Cap. 01 • Parágrafo 01", "Narração", "夜色沉沉，{PlayerName}盘膝坐在蒲团上，引导灵气汇入丹田。", "A noite caía pesada e {PlayerName} guiou o Qi Espiritual até seu Dantian.", 280),
            new MaterialBlockItem("wiki-001", "LoreWiki", "Reinos • Estágio I", "Enciclopédia", "炼气期：修士吸纳天地灵气入体，开启修仙之路的基础境界。", "Refinamento de Qi: reino inicial em que o cultivador absorve Qi Espiritual.", 220)
        };
    }
}

public sealed record MaterialBlockItem(
    string Id,
    string Category,
    string Reference,
    string BlockType,
    string SourceText,
    string TranslatedText,
    int MaxChars,
    bool Approved = false);
