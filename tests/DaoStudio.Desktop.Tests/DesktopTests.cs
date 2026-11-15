using System.Net.Http.Json;
using DaoStudio.Bridge;
using DaoStudio.Bridge.Abstractions;
using DaoStudio.Contracts;
using DaoStudio.Desktop.DependencyInjection;
using DaoStudio.Desktop.Services;
using DaoStudio.Desktop.ViewModels;
using DaoStudio.Engine;
using DaoStudio.Engine.Abstractions;
using DaoStudio.Patcher;
using DaoStudio.Patcher.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DaoStudio.Desktop.Tests;

public sealed class DesktopTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DaoStudio.Desktop.Tests", Guid.NewGuid().ToString("N"));
    public DesktopTests() => Directory.CreateDirectory(root);
    public void Dispose()
    {
        var full = Path.GetFullPath(root);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DaoStudio.Desktop.Tests")) + Path.DirectorySeparatorChar;
        if (full.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
    }
    private ServiceProvider Provider() => new ServiceCollection().AddDaoStudioDesktop(Path.Combine(root, "cache"))
        .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    private DesktopResources Resources => new(AppContext.BaseDirectory);
    private sealed class Locator(DetectedGameInstall? game) : ISteamLocator
    {
        public Task<DetectedGameInstall?> FindGameAsync(GameProfileManifest manifest, CancellationToken ct = default) => Task.FromResult(game);
    }
    private sealed class Host : IBridgeHostController
    {
        public bool IsRunning { get; private set; }
        public int Port => IsRunning ? 12345 : 0;
        public string TokenSummary => IsRunning ? "ABCDEF12…" : "—";
        public BridgeTelemetry Telemetry { get; } = new();
        public Task StartAsync() { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
        public BridgeTelemetrySnapshot GetSnapshot() => Telemetry.GetSnapshot();
    }
    private LauncherViewModel Launcher(DetectedGameInstall? game = null, Host? host = null) =>
        new(new Locator(game), new PatchTransactionManager(), host ?? new Host(), Resources);
    private DetectedGameInstall Game(bool executable = true)
    {
        var dir = Path.Combine(root, "game"); Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "guigubahuang.exe");
        if (executable) File.WriteAllText(exe, "original executable");
        return new("guigubahuang", "1468810", dir, exe, executable);
    }
    private static CacheKey Key(string source) => new("guigubahuang", "pt-BR",
        new Sha256TemplateHasher().ComputeHash(new StringNormalizer().Normalize(new TagMasker().Mask(source).MaskedTemplate)), 1);
    private async Task Load(WorkbenchViewModel vm, params LocalizationSegment[] segments)
    {
        var path = Path.Combine(root, "input.json");
        await File.WriteAllTextAsync(path, new JsonLocalizationParser().Export(segments));
        await vm.ImportFileAsync(path);
    }

    [Fact]
    public async Task CompleteContainerResolvesWithoutOpeningSocket()
    {
        await using var provider = Provider();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        Assert.Same(vm.Launcher, provider.GetRequiredService<LauncherViewModel>());
        Assert.Same(vm.Workbench, provider.GetRequiredService<WorkbenchViewModel>());
        Assert.Equal(3, provider.GetServices<ILocalizationFileParser>().Count());
        Assert.NotNull(provider.GetRequiredService<IManifestDownloader>());
        Assert.NotNull(provider.GetRequiredService<ISteamLocator>());
        Assert.False(provider.GetRequiredService<IBridgeHostController>().IsRunning);
        Assert.True(Directory.Exists(Path.Combine(root, "cache")));
        vm.ShowWorkbenchCommand.Execute(null); Assert.Same(vm.Workbench, vm.CurrentView);
        vm.ShowLauncherCommand.Execute(null); Assert.Same(vm.Launcher, vm.CurrentView);
    }
    [Theory]
    [InlineData("", false)] [InlineData("short", false)] [InlineData("with space!", false)] [InlineData("DAO-12345678", true)]
    public async Task LicenseValidation(string license, bool valid)
    {
        var host = new Host(); var vm = Launcher(host: host); vm.LicenseKey = license;
        Assert.Equal(valid, vm.IsLicenseValid);
        await vm.StartBridgeCommand.ExecuteAsync(null);
        Assert.True(host.IsRunning);
        Assert.Equal("", vm.ErrorMessage);
    }
    [Fact]
    public async Task MissingGameAndMissingExecutableHaveClearErrors()
    {
        var vm = Launcher();
        await vm.DetectGameCommand.ExecuteAsync(null); Assert.Contains("Steam ausente ou jogo não encontrado", vm.ErrorMessage);
        vm.LicenseKey = "DAO-12345678";
        await vm.InstallAndActivatePatchCommand.ExecuteAsync(null); Assert.Contains("não detectado", vm.ErrorMessage);
        vm = Launcher(Game(false));
        await vm.DetectGameCommand.ExecuteAsync(null); Assert.Contains("Executável ausente", vm.ErrorMessage);
    }
    [Fact]
    public async Task PatchAndRollbackRestoreOriginalAndRemoveAddedFiles()
    {
        var game = Game(); var host = new Host(); var vm = Launcher(game, host);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(Path.Combine(stage, "Mods"));
        await File.WriteAllTextAsync(Path.Combine(stage, "guigubahuang.exe"), "patched");
        await File.WriteAllTextAsync(Path.Combine(stage, "Mods", "plugin.dll"), "plugin");
        vm.StagedFilesDirectory = stage; vm.LicenseKey = "DAO-12345678";
        await vm.DetectGameCommand.ExecuteAsync(null); Assert.Equal(game, vm.DetectedGame);
        await vm.InstallAndActivatePatchCommand.ExecuteAsync(null); Assert.Equal("", vm.ErrorMessage); Assert.True(vm.PatchApplied);
        Assert.Equal("patched", await File.ReadAllTextAsync(game.ExecutablePath));
        await vm.InstallAndActivatePatchCommand.ExecuteAsync(null); Assert.Contains("já aplicado", vm.ErrorMessage);
        await vm.StartBridgeCommand.ExecuteAsync(null);
        await vm.RollbackPatchCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ErrorMessage); Assert.False(vm.PatchApplied); Assert.False(host.IsRunning);
        Assert.Equal("original executable", await File.ReadAllTextAsync(game.ExecutablePath));
        Assert.False(Directory.Exists(Path.Combine(game.InstallDirectory, "Mods")));
        Assert.False(new PatchTransactionManager().IsPatchApplied(game.InstallDirectory));
    }
    [Fact]
    public async Task InvalidStagingDoesNotModifyGame()
    {
        var game = Game(); var vm = Launcher(game); vm.LicenseKey = "DAO-12345678";
        await vm.DetectGameCommand.ExecuteAsync(null);
        await vm.InstallAndActivatePatchCommand.ExecuteAsync(null);
        Assert.Contains("pasta", vm.ErrorMessage);
        Assert.Equal("original executable", await File.ReadAllTextAsync(game.ExecutablePath));
        Assert.False(new PatchTransactionManager().IsPatchApplied(game.InstallDirectory));
    }
    [Fact]
    public async Task MockBridgeLifecycleAndLiveTelemetry()
    {
        var host = new Host(); var vm = Launcher(host: host); vm.LicenseKey = "DAO-12345678";
        await vm.StartBridgeCommand.ExecuteAsync(null);
        host.Telemetry.RecordHit("L0"); host.Telemetry.RecordHit("L1"); host.Telemetry.RecordMiss();
        host.Telemetry.RecordHit("L2"); host.Telemetry.RecordMiss(); host.Telemetry.RecordFallback();
        host.Telemetry.SetCircuitState(CircuitBreakerState.Open);
        vm.RefreshTelemetry();
        Assert.Equal(1, vm.L0Hits); Assert.Equal(1, vm.L1Hits); Assert.Equal(1, vm.L2Hits);
        Assert.Equal(75, vm.CacheHitPercent); Assert.Equal(CircuitBreakerState.Open, vm.CircuitState);
        Assert.Equal(12345, vm.BridgePort); Assert.Contains("…", vm.TokenSummary);
        Assert.Contains(vm.DiagnosticLogs, s => s.Contains("Fallback:"));
        await vm.StopBridgeCommand.ExecuteAsync(null); Assert.Equal(0, vm.BridgePort); Assert.Equal("Parada", vm.BridgeStatus);
    }
    [Fact]
    public async Task RealBridgeCanStartStopRestartAndServeCachedTranslation()
    {
        await using var provider = Provider();
        // Test the real server's authenticated HTTP path, using an ephemeral port.
        var cache = provider.GetRequiredService<ITranslationCache>();
        await cache.SetAsync(Key("你好"), "Olá");
        await using (var server = new BridgeServer(provider.GetRequiredService<BridgeTranslationOrchestrator>(), provider.GetRequiredService<BridgeTelemetry>(), 0))
        {
            await server.StartAsync(); Assert.True(server.Port > 0);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
            http.DefaultRequestHeaders.Add("X-Bridge-Token", server.SessionToken);
            using var response = await http.PostAsJsonAsync("/bridge/translate", new BridgeTranslateRequest("guigubahuang", "你好"));
            response.EnsureSuccessStatusCode();
            Assert.Equal("Olá", (await response.Content.ReadFromJsonAsync<BridgeTranslateResponse>())!.TranslatedText);
        }
        var vm = provider.GetRequiredService<LauncherViewModel>(); vm.LicenseKey = "DAO-12345678";
        await vm.StartBridgeCommand.ExecuteAsync(null); Assert.True(vm.BridgePort > 0);
        vm.RefreshTelemetry(); Assert.Equal(1, vm.L0Hits);
        await vm.StopBridgeCommand.ExecuteAsync(null); Assert.Equal(0, vm.BridgePort);
        await vm.StartBridgeCommand.ExecuteAsync(null); Assert.True(vm.BridgePort > 0);
        await vm.StopBridgeCommand.ExecuteAsync(null);
    }
    [Theory]
    [InlineData(".json", "{\"intro\":\"你好\"}")]
    [InlineData(".csv", "key,text\nintro,你好\n")]
    [InlineData(".txt", "intro=你好\n")]
    public async Task ImportsRegisteredFormats(string extension, string content)
    {
        await using var provider = Provider(); var vm = provider.GetRequiredService<WorkbenchViewModel>();
        var path = Path.Combine(root, "import" + extension); await File.WriteAllTextAsync(path, content);
        await vm.ImportFileAsync(path);
        Assert.Equal("你好", Assert.Single(vm.Segments).SourceText); Assert.Equal("intro", vm.SelectedSegment!.Key);
    }
    [Theory]
    [InlineData("{PlayerName}你好", "Olá")]
    [InlineData("{PlayerName}你好", "{PlayerName}{PlayerName}Olá")]
    [InlineData("{0}你好", "Olá")]
    [InlineData("{0}你好", "{0}{0}Olá")]
    [InlineData("{PlayerName}你好", "{0}Olá")]
    [InlineData("<b>你好</b>", "Olá")]
    [InlineData("<b>你好</b>", "<b><b>Olá</b>")]
    [InlineData("你好", "")]
    public async Task InvalidApprovalNeverWritesCache(string source, string translated)
    {
        await using var provider = Provider(); var vm = provider.GetRequiredService<WorkbenchViewModel>();
        await Load(vm, new LocalizationSegment("1", "intro", source)); vm.TranslationText = translated;
        await vm.ApproveSegmentCommand.ExecuteAsync(null);
        Assert.Contains("Aprovação bloqueada", vm.ValidationError);
        Assert.False(vm.Segments[0].Approved);
        Assert.False((await provider.GetRequiredService<L1SqliteCache>().TryGetAsync(Key(source))).Hit);
        Assert.False((await provider.GetRequiredService<L0MemoryCache>().TryGetAsync(Key(source))).Hit);
    }
    [Theory]
    [InlineData(".json")] [InlineData(".csv")] [InlineData(".txt")]
    public async Task ApprovalPersistsBothTiersAdvancesAndExportsReviewMetadata(string extension)
    {
        await using var provider = Provider(); var vm = provider.GetRequiredService<WorkbenchViewModel>();
        const string source = "{PlayerName}你好{0}";
        await Load(vm, new LocalizationSegment("1", "intro", source), new("2", "next", "再见"));
        vm.TranslationText = "{0}, olá {PlayerName}";
        await vm.ApproveSegmentCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ValidationError); Assert.True(vm.Segments[0].Approved);
        Assert.Equal("2", vm.SelectedSegment!.Id); Assert.Equal("1 de 2 aprovados", vm.Progress);
        var l1 = await provider.GetRequiredService<L1SqliteCache>().TryGetAsync(Key(source));
        Assert.True(l1.Hit); Assert.Equal("{{VAR_1}}, olá {{VAR_0}}", l1.TranslatedTemplate);
        Assert.True((await provider.GetRequiredService<L0MemoryCache>().TryGetAsync(Key(source))).Hit);
        var path = Path.Combine(root, "export" + extension);
        await vm.ExportFileAsync(path); await vm.ImportFileAsync(path);
        Assert.True(vm.Segments[0].Approved); Assert.Equal(source, vm.Segments[0].SourceText);
        Assert.Equal("{0}, olá {PlayerName}", vm.Segments[0].TranslatedText);
    }
    [Fact]
    public async Task GlossaryShowsCanonicalTermsAndBlocksForbiddenOrOverlongText()
    {
        await using var provider = Provider();
        var glossary = new GlossaryMatcher(new[] { new GlossaryEntry("道心", "dao xin", "Coração do Dao", "", "", "", new[] { "coração da estrada" }, 30) });
        var vm = new WorkbenchViewModel(provider.GetServices<ILocalizationFileParser>(), glossary, new TagMasker(), new StringNormalizer(),
            new Sha256TemplateHasher(), provider.GetRequiredService<ITranslationCache>(), Resources);
        await Load(vm, new LocalizationSegment("1", "term", "道心"));
        Assert.Equal("Coração do Dao", Assert.Single(vm.CanonicalTerms).PtBr);
        vm.TranslationText = "coração da estrada";
        Assert.Contains("Termos proibidos", vm.GlossaryWarnings);
        await vm.ApproveSegmentCommand.ExecuteAsync(null); Assert.False(vm.Segments[0].Approved);
        vm.TranslationText = new string('a', 31); Assert.Contains("maxChars", vm.GlossaryWarnings);
        await vm.ApproveSegmentCommand.ExecuteAsync(null); Assert.False(vm.Segments[0].Approved);
        vm.TranslationText = "Coração do Dao"; await vm.ApproveSegmentCommand.ExecuteAsync(null);
        Assert.True(vm.Segments[0].Approved);
        vm.TranslationText = "edição"; Assert.False(vm.Segments[0].Approved);
    }
    [Fact]
    public async Task CacheSurvivesContainerRecreationAndSeedIsOptional()
    {
        await using (var provider = Provider())
        {
            var vm = provider.GetRequiredService<WorkbenchViewModel>();
            var expectedSeedCount = new JsonLocalizationParser().Parse(await File.ReadAllTextAsync(Resources.SeedPath)).Count;
            Assert.True(expectedSeedCount >= 40);
            await vm.LoadSeedCommand.ExecuteAsync(null); Assert.Equal(expectedSeedCount, vm.Segments.Count);
            await provider.GetRequiredService<ITranslationCache>().SetAsync(Key("你好"), "Olá");
        }
        await using var reopened = Provider();
        var hit = await reopened.GetRequiredService<ITranslationCache>().TryGetAsync(Key("你好"));
        Assert.True(hit.Hit); Assert.Equal("Olá", hit.TranslatedTemplate);
    }
    [Fact]
    public async Task RealSteamLocatorDetectsTemporarySteamLibrary()
    {
        var steam = Path.Combine(root, "steam");
        var apps = Path.Combine(steam, "steamapps");
        var game = Path.Combine(apps, "common", "Tale of Immortal");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "guigubahuang.exe"), "exe");
        await File.WriteAllTextAsync(Path.Combine(apps, "appmanifest_1468810.acf"),
            "\"AppState\" { \"appid\" \"1468810\" \"installdir\" \"Tale of Immortal\" }");
        var vm = new LauncherViewModel(new WindowsSteamLocator(new[] { steam }), new PatchTransactionManager(), new Host(), Resources);
        await vm.DetectGameCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ErrorMessage); Assert.Equal(game, vm.DetectedGame!.InstallDirectory);
    }
    [Fact]
    public async Task ConflictingPatchIsAtomicAndLeavesNoBackup()
    {
        var game = Game(); var vm = Launcher(game); vm.LicenseKey = "DAO-12345678";
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        await File.WriteAllTextAsync(Path.Combine(stage, "guigubahuang.exe"), "patched");
        await File.WriteAllTextAsync(Path.Combine(stage, "Mods"), "conflict");
        Directory.CreateDirectory(Path.Combine(game.InstallDirectory, "Mods"));
        vm.StagedFilesDirectory = stage;
        await vm.DetectGameCommand.ExecuteAsync(null); await vm.InstallAndActivatePatchCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.ErrorMessage); Assert.False(vm.PatchApplied);
        Assert.Equal("original executable", await File.ReadAllTextAsync(game.ExecutablePath));
        Assert.False(new PatchTransactionManager().IsPatchApplied(game.InstallDirectory));
    }
    [Fact]
    public async Task InvalidImportPreservesWorkAndCommandsSurfaceErrors()
    {
        await using var provider = Provider(); var vm = provider.GetRequiredService<WorkbenchViewModel>();
        await Load(vm, new LocalizationSegment("1", "intro", "你好"));
        vm.FilePath = Path.Combine(root, "broken.json"); await File.WriteAllTextAsync(vm.FilePath, "{");
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Contains("Falha no arquivo", vm.ValidationError); Assert.Single(vm.Segments);
        vm.FilePath = Path.Combine(root, "unsupported.xml"); await vm.ExportCommand.ExecuteAsync(null);
        Assert.Contains("Formato não suportado", vm.ValidationError);
    }
    [Fact]
    public async Task RichTextApprovalPreservesAllTagsAndVariables()
    {
        await using var provider = Provider(); var vm = provider.GetRequiredService<WorkbenchViewModel>();
        const string source = "<color=#FF0000>{PlayerName}</color>你好{0}";
        await Load(vm, new LocalizationSegment("1", "intro", source));
        vm.TranslationText = "<color=#FF0000>{PlayerName}</color>, olá {0}";
        await vm.ApproveSegmentCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ValidationError); Assert.True(vm.Segments[0].Approved);
        var cached = await provider.GetRequiredService<ITranslationCache>().TryGetAsync(Key(source));
        var mask = new TagMasker().Mask(source);
        Assert.Equal(vm.TranslationText, new TagMasker().Unmask(cached.TranslatedTemplate!, mask.Placeholders));
    }
    [Fact]
    public void MissingProfileUsesSafeFallback()
    {
        var resources = new DesktopResources(root); var profile = resources.LoadProfile();
        Assert.Equal("1468810", profile.SteamAppId); Assert.Equal("guigubahuang.exe", profile.ExecutableName);
        Assert.Contains("fallback", resources.ProfileWarning);
    }

    [Fact]
    public async Task MultiGameCatalogLoadsAllFiveGamesAndSynchronizesAcrossViews()
    {
        await using var provider = Provider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        var ids = main.AvailableGames.Select(g => g.GameId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "guigubahuang", "michangsheng", "xiuxiansimulator", "herosadventure", "wanderingsword" })
            Assert.Contains(expected, ids);

        var miChangSheng = main.AvailableGames.Single(g => g.GameId == "michangsheng");
        main.SelectedGame = miChangSheng;
        Assert.Same(miChangSheng, main.Launcher.SelectedGame);
        Assert.Same(miChangSheng, main.Workbench.SelectedGame);
        Assert.Contains("1189490", main.Launcher.GameMetadataSummary);
        Assert.Contains("bepinex", main.Launcher.GameMetadataSummary);

        await main.Workbench.LoadSeedCommand.ExecuteAsync(null);
        Assert.True(main.Workbench.Segments.Count >= 8);
        Assert.Equal(main.Workbench.Segments.Count, main.Workbench.FilteredSegments.Count);

        var steam = Path.Combine(root, "steam-multi");
        var apps = Path.Combine(steam, "steamapps");
        var herosAdventure = main.AvailableGames.Single(g => g.GameId == "herosadventure");
        var gameDir = Path.Combine(apps, "common", "Heros Adventure");
        Directory.CreateDirectory(gameDir);
        await File.WriteAllTextAsync(Path.Combine(gameDir, herosAdventure.ExecutableName), "exe");
        await File.WriteAllTextAsync(Path.Combine(apps, $"appmanifest_{herosAdventure.SteamAppId}.acf"),
            "\"AppState\" { \"appid\" \"1948980\" \"installdir\" \"Heros Adventure\" }");
        var launcher = new LauncherViewModel(new WindowsSteamLocator(new[] { steam }), new PatchTransactionManager(), new Host(), Resources);
        launcher.SelectedGame = launcher.AvailableGames.Single(g => g.GameId == "herosadventure");
        await launcher.DetectGameCommand.ExecuteAsync(null);
        Assert.Equal("", launcher.ErrorMessage);
        Assert.Equal(gameDir, launcher.DetectedGame!.InstallDirectory);
    }

    [Fact]
    public async Task WorkbenchExtractsPlaceholderChipsInsertsTermsAndFiltersSegments()
    {
        await using var provider = Provider();
        var vm = provider.GetRequiredService<WorkbenchViewModel>();
        var ha = vm.AvailableGames.Single(g => g.GameId == "herosadventure");
        vm.SelectedGame = ha;
        await Load(vm,
            new LocalizationSegment("ha-1", "story.intro", "{PlayerName}踏入江湖", "{PlayerName} entrou no Jianghu", true, "dialogue"),
            new LocalizationSegment("ha-2", "skill.jump", "施展轻功{0}", "", false, "tooltip"));

        Assert.Equal("ha-2", vm.SelectedSegment!.Id);
        Assert.Contains("{0}", vm.DetectedPlaceholders);
        var qinggong = Assert.Single(vm.CanonicalTerms, t => t.Zh == "轻功");

        vm.InsertGlossaryTermCommand.Execute(qinggong);
        vm.InsertPlaceholderCommand.Execute("{0}");
        Assert.Contains("Técnica de Leveza", vm.TranslationText);
        Assert.Contains("{0}", vm.TranslationText);

        await vm.ApproveSegmentCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ValidationError);
        Assert.Equal(2, vm.ApprovedCount);
        Assert.Equal(100d, vm.ProgressPercent);

        var key = new CacheKey(ha.GameId, "pt-BR",
            new Sha256TemplateHasher().ComputeHash(new StringNormalizer().Normalize(new TagMasker().Mask("施展轻功{0}").MaskedTemplate)),
            ha.GlossaryVersion);
        Assert.True((await provider.GetRequiredService<L1SqliteCache>().TryGetAsync(key)).Hit);

        vm.SetFilterCommand.Execute("Pendentes");
        Assert.Empty(vm.FilteredSegments);
        vm.SetFilterCommand.Execute("Aprovados");
        Assert.Equal(2, vm.FilteredSegments.Count);
        vm.SearchQuery = "skill.jump";
        Assert.Single(vm.FilteredSegments);
    }

    [Fact]
    public async Task LogoAssetsExistAndModulesSwitchCleanly()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "daostudio-logo.png")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "daostudio-logo.ico")));

        await using var provider = Provider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        Assert.Same(main.Tour, main.CurrentView);
        Assert.True(main.IsTourActive);
        Assert.False(main.IsSidebarVisible);
        Assert.False(main.IsMaterialsActive);

        main.ShowLauncherCommand.Execute(null);
        Assert.Same(main.Launcher, main.CurrentView);
        Assert.True(main.IsLauncherActive);
        Assert.True(main.IsSidebarVisible);

        main.ShowMaterialsCommand.Execute(null);
        Assert.Same(main.Materials, main.CurrentView);
        Assert.True(main.IsMaterialsActive);
        Assert.True(main.IsSidebarVisible);
        Assert.False(main.IsTourActive);

        main.ShowTourCommand.Execute(null);
        Assert.Same(main.Tour, main.CurrentView);
        Assert.True(main.IsTourActive);
        Assert.False(main.IsSidebarVisible);

        main.Tour.SkipTourCommand.Execute(null);
        Assert.Same(main.Launcher, main.CurrentView);
        Assert.True(main.IsSidebarVisible);

        main.ShowTourCommand.Execute(null);

        var wuxia = main.AvailableGames.Single(g => g.GameId == "wanderingsword");
        main.SelectedGame = wuxia;
        Assert.Same(wuxia, main.Materials.SelectedGame);
        Assert.Same(wuxia, main.Workbench.SelectedGame);
        Assert.Same(wuxia, main.Launcher.SelectedGame);
        Assert.Same(wuxia, main.Tour.SelectedGame);
    }

    [Fact]
    public async Task CustomizableTourInfersExperienceAndConfiguresTargetModules()
    {
        await using var provider = Provider();
        var main = provider.GetRequiredService<MainWindowViewModel>();

        Assert.Equal(0, main.Tour.CurrentStage);
        Assert.False(main.IsSidebarVisible);
        Assert.True(main.Tour.IsStage0Intro);
        Assert.False(main.Tour.IsNode1Active);
        Assert.Equal(0, main.Tour.WizardProgressPercent);

        main.Tour.StartWizardCommand.Execute(null);
        Assert.Equal(1, main.Tour.CurrentStage);
        Assert.True(main.Tour.IsStage1Intent);
        Assert.Equal(25, main.Tour.WizardProgressPercent);

        main.Tour.SelectIntentCommand.Execute("manhua");
        Assert.Equal(2, main.Tour.CurrentStage);
        Assert.True(main.Tour.IsStage2Universe);
        Assert.True(main.Tour.IsNode1Active);
        Assert.True(main.Tour.IsManhuaIntent);
        Assert.Contains("Manhua", main.Tour.InferredPersonaBadge);

        var wuxia = main.AvailableGames.Single(g => g.GameId == "wanderingsword");
        main.Tour.SelectUniverseCommand.Execute(wuxia);
        Assert.Equal(3, main.Tour.CurrentStage);
        Assert.True(main.Tour.IsStage3Style);
        Assert.True(main.Tour.IsNode2Active);
        Assert.Same(wuxia, main.Tour.SelectedGame);

        main.Tour.SelectBubblePresetCommand.Execute("42");
        Assert.Equal(42, main.Tour.CustomBubbleMaxChars);
        Assert.True(main.Tour.IsBubbleCompact);

        main.Tour.ChooseStyleAndAdvanceCommand.Execute("Avancado");
        Assert.Equal(4, main.Tour.CurrentStage);
        Assert.True(main.Tour.IsStage4Blueprint);
        Assert.Equal(100, main.Tour.WizardProgressPercent);
        Assert.True(main.Tour.IsAdvancedLevel);
        Assert.True(main.Tour.IsNode1Active);
        Assert.True(main.Tour.IsNode2Active);
        Assert.True(main.Tour.IsNode3Active);
        Assert.True(main.Tour.IsNode4Active);

        await main.Tour.ApplyAndStartCommand.ExecuteAsync(null);
        Assert.Same(main.Materials, main.CurrentView);
        Assert.Same(wuxia, main.Materials.SelectedGame);
        Assert.True(main.Materials.IsManhuaSelected);
        Assert.Equal(42, main.Materials.DefaultBubbleMaxChars);
        Assert.Contains("Manhua", main.Materials.ExportedPreview);

        main.ShowTourCommand.Execute(null);
        main.Tour.ResetTourCommand.Execute(null);
        Assert.Equal(0, main.Tour.CurrentStage);
        Assert.False(main.Tour.IsNode1Active);

        main.Tour.SelectIntentCommand.Execute("webnovel");
        await main.Tour.ApplyAndStartCommand.ExecuteAsync(null);
        Assert.Same(main.Materials, main.CurrentView);
        Assert.True(main.Materials.IsWebnovelSelected);

        main.ShowTourCommand.Execute(null);
        var herosAdventure = main.AvailableGames.Single(g => g.GameId == "herosadventure");
        main.Tour.SelectedGame = herosAdventure;
        main.Tour.SelectIntentCommand.Execute("cat");
        await main.Tour.ApplyAndStartCommand.ExecuteAsync(null);
        Assert.Same(main.Workbench, main.CurrentView);
        Assert.Same(herosAdventure, main.Workbench.SelectedGame);
        Assert.True(main.Workbench.Segments.Count >= 8);

        main.ShowTourCommand.Execute(null);
        main.Tour.SelectIntentCommand.Execute("player");
        await main.Tour.ApplyAndStartCommand.ExecuteAsync(null);
        Assert.Same(main.Launcher, main.CurrentView);
    }

    [Fact]
    public async Task MaterialsModuleEnforcesManhuaBubbleLimitExportsBilingualAndSharesCache()
    {
        await using var provider = Provider();
        var vm = provider.GetRequiredService<MaterialsViewModel>();

        Assert.True(vm.IsManhuaSelected);
        Assert.NotEmpty(vm.FilteredBlocks);

        // Enforce bubble typesetting limit
        var first = vm.FilteredBlocks[0];
        vm.SelectedBlock = first;
        vm.TranslationText = new string('x', first.MaxChars + 10);
        Assert.True(vm.IsOverCharBudget);
        await vm.ApproveBlockCommand.ExecuteAsync(null);
        Assert.Contains("Orçamento de typesetting excedido", vm.ValidationMessage);
        Assert.False(first.Approved);

        // Valid approval writes to shared L1 cache
        var dialogue = vm.FilteredBlocks.First(b => b.Id == "mh-002");
        vm.SelectedBlock = dialogue;
        vm.TranslationText = "{PlayerName}, enfim no Estabelecimento de Fundação!";
        await vm.ApproveBlockCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ValidationMessage);

        var cached = await provider.GetRequiredService<ITranslationCache>().TryGetAsync(Key(dialogue.SourceText));
        Assert.True(cached.Hit);

        // Switch to Webnovel, split raw text, and export bilingual Markdown
        vm.SelectCategoryCommand.Execute("Webnovel");
        Assert.True(vm.IsWebnovelSelected);
        vm.RawInputText = "林风走出宗门大门。\n前方灵气滚滚而来。";
        vm.SplitRawTextCommand.Execute(null);
        Assert.Equal(2, vm.FilteredBlocks.Count(b => b.Id.StartsWith("custom-", StringComparison.Ordinal)));

        var exportFile = Path.Combine(root, "chapter-bilingual.md");
        await vm.ExportToFileCommand.ExecuteAsync(exportFile);
        Assert.True(File.Exists(exportFile));
        var content = await File.ReadAllTextAsync(exportFile);
        Assert.Contains("Webnovel", content);
        Assert.Contains("> [ZH]", content);
    }
}


