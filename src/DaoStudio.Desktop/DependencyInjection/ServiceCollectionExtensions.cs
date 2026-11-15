using DaoStudio.Bridge;
using DaoStudio.Bridge.Abstractions;
using DaoStudio.Contracts;
using DaoStudio.Desktop.Services;
using DaoStudio.Desktop.ViewModels;
using DaoStudio.Engine;
using DaoStudio.Engine.Abstractions;
using DaoStudio.Patcher;
using DaoStudio.Patcher.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DaoStudio.Desktop.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDaoStudioDesktop(this IServiceCollection services, string? dataDirectory = null)
    {
        var dataDir = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaoStudio");
        Directory.CreateDirectory(dataDir);
        services.AddSingleton(new DesktopResources(AppContext.BaseDirectory));
        services.AddSingleton<ITagMasker, TagMasker>();
        services.AddSingleton<IStringNormalizer, StringNormalizer>();
        services.AddSingleton<ITemplateHashCalculator, Sha256TemplateHasher>();
        services.AddSingleton<L0MemoryCache>();
        services.AddSingleton(_ => new L1SqliteCache(Path.Combine(dataDir, "translations.db"), LoadKey(dataDir)));
        services.AddSingleton<ITranslationCache, TieredTranslationCache>();
        services.AddSingleton<IGlossaryMatcher>(_ => GlossaryMatcher.FromJsonFile(Path.Combine(AppContext.BaseDirectory, "data", "glossary", "guigubahuang-glossary.json")));
        services.AddSingleton<ILocalizationFileParser, JsonLocalizationParser>();
        services.AddSingleton<ILocalizationFileParser, CsvLocalizationParser>();
        services.AddSingleton<ILocalizationFileParser, TextLineParser>();
        services.AddSingleton<ISteamLocator>(_ => new WindowsSteamLocator());
        services.AddSingleton<IPatchTransactionManager, PatchTransactionManager>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IManifestDownloader, ManifestDownloader>();
        services.AddSingleton<BridgeTelemetry>();
        services.AddSingleton<ICloudTranslationClient, OfflineCloudClient>();
        services.AddSingleton<MicroBatcher>();
        services.AddSingleton<PostTranslationValidator>();
        services.AddSingleton<BridgeTranslationOrchestrator>();
        services.AddSingleton<IBridgeHostController, BridgeHostController>();
        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<WorkbenchViewModel>();
        services.AddSingleton<MaterialsViewModel>();
        services.AddSingleton<TourViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
    private static byte[] LoadKey(string directory)
    {
        var path = Path.Combine(directory, "cache.key");
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(key);
        return key;
    }
    private sealed class OfflineCloudClient : ICloudTranslationClient
    {
        public CircuitBreakerState CircuitState => CircuitBreakerState.Open;
        public OfflineCloudClient(BridgeTelemetry telemetry) => telemetry.SetCircuitState(CircuitState);
        public Task<TranslationBatchResponse> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct = default)
            => Task.FromResult(new TranslationBatchResponse(request.GameId, request.TargetLang, Array.Empty<TranslationResultItem>()));
    }
}
