using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaoStudio.Bridge.Abstractions;
using DaoStudio.Contracts;
using DaoStudio.Desktop.Services;
using DaoStudio.Patcher.Abstractions;

namespace DaoStudio.Desktop.ViewModels;

public partial class LauncherViewModel : ObservableObject
{
    private readonly ISteamLocator locator;
    private readonly IPatchTransactionManager patches;
    private readonly IBridgeHostController bridge;
    private readonly SemaphoreSlim operations = new(1);
    private long lastFallbacks;

    public IReadOnlyList<GameProfileManifest> AvailableGames { get; }
    [ObservableProperty] private GameProfileManifest selectedGame;
    [ObservableProperty] private string licenseKey = "";
    [ObservableProperty] private string sessionStatus = "Modo Debug ativo • chave de licença dispensada";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private DetectedGameInstall? detectedGame;
    [ObservableProperty] private bool patchApplied;
    [ObservableProperty] private string stagedFilesDirectory = "";
    [ObservableProperty] private string bridgeStatus = "Parada";
    [ObservableProperty] private int bridgePort;
    [ObservableProperty] private string tokenSummary = "—";
    [ObservableProperty] private long l0Hits;
    [ObservableProperty] private long l1Hits;
    [ObservableProperty] private long l2Hits;
    [ObservableProperty] private double cacheHitPercent;
    [ObservableProperty] private CircuitBreakerState circuitState;
    public ObservableCollection<string> DiagnosticLogs { get; } = new();

    public bool IsLicenseValid => Regex.IsMatch(LicenseKey.Trim(), @"\A[A-Za-z0-9][A-Za-z0-9-]{7,127}\z", RegexOptions.CultureInvariant);

    public string GameMetadataSummary =>
        $"Steam AppID {SelectedGame.SteamAppId}  •  {SelectedGame.Engine}  •  Loader: {SelectedGame.Loader.Type} ({SelectedGame.Loader.Version})  •  Fonte: {SelectedGame.FontAsset.License}";

    public string ExecAndLoaderSummary =>
        $"Executável esperado: {SelectedGame.ExecutableName}  |  Glossário v{SelectedGame.GlossaryVersion}";

    public string DetectionStatusBadge => DetectedGame is null
        ? "Aguardando detecção"
        : (PatchApplied ? "Detectado • Patch Ativo" : "Detectado • Original");

    public string CurrentStepHint =>
        DetectedGame is null ? $"Passo 1 de 2: Clique em 'Detectar na Steam' para localizar {SelectedGame.DisplayName} (ou clique em 'Iniciar Bridge' direto)."
        : !bridge.IsRunning ? "Passo 2 de 2: Clique em 'Iniciar Bridge' para traduzir em tempo real (ou aplique o patch)."
        : $"Tudo pronto! Bridge rodando na porta {BridgePort} para {SelectedGame.DisplayName}.";

    public LauncherViewModel(ISteamLocator locator, IPatchTransactionManager patches, IBridgeHostController bridge, DesktopResources resources)
    {
        this.locator = locator;
        this.patches = patches;
        this.bridge = bridge;
        AvailableGames = resources.LoadAllProfiles();
        selectedGame = AvailableGames[0];
        if (resources.ProfileWarning.Length > 0) Log(resources.ProfileWarning);
        Log($"Modo offline: cache local disponível ({AvailableGames.Count} jogos no catálogo); autenticação remota não configurada.");
    }

    partial void OnSelectedGameChanged(GameProfileManifest value)
    {
        DetectedGame = null;
        PatchApplied = false;
        ErrorMessage = "";
        NotifyContextProperties();
        Log($"Jogo selecionado: {value.DisplayName} ({value.Engine} / {value.Loader.Type})");
    }

    partial void OnDetectedGameChanged(DetectedGameInstall? value) => NotifyContextProperties();
    partial void OnPatchAppliedChanged(bool value) => NotifyContextProperties();
    partial void OnBridgeStatusChanged(string value) => OnPropertyChanged(nameof(CurrentStepHint));

    partial void OnLicenseKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsLicenseValid));
        OnPropertyChanged(nameof(CurrentStepHint));
        SessionStatus = string.IsNullOrWhiteSpace(value)
            ? "Modo Debug ativo • chave de licença dispensada"
            : IsLicenseValid
                ? "Formato válido • sessão local offline"
                : "Formato de chave opcional ignorado em modo debug.";
    }

    private void NotifyContextProperties()
    {
        OnPropertyChanged(nameof(GameMetadataSummary));
        OnPropertyChanged(nameof(ExecAndLoaderSummary));
        OnPropertyChanged(nameof(DetectionStatusBadge));
        OnPropertyChanged(nameof(CurrentStepHint));
    }

    private void Log(string message)
    {
        DiagnosticLogs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (DiagnosticLogs.Count > 200) DiagnosticLogs.RemoveAt(DiagnosticLogs.Count - 1);
    }

    private async Task RunAsync(Func<Task> action)
    {
        await operations.WaitAsync();
        try { ErrorMessage = ""; await action(); }
        catch (Exception ex) { ErrorMessage = ex.Message; Log(ErrorMessage); }
        finally { operations.Release(); }
    }

    private void RequireLicense()
    {
        // Modo debug: chave de licença dispensada temporariamente.
    }

    [RelayCommand]
    private void ClearLogs() => DiagnosticLogs.Clear();

    [RelayCommand]
    private Task DetectGameAsync() => RunAsync(async () =>
    {
        var profile = SelectedGame;
        DetectedGame = null;
        PatchApplied = false;
        DetectedGame = await locator.FindGameAsync(profile);
        if (DetectedGame is null)
            throw new InvalidOperationException($"Steam ausente ou jogo não encontrado ({profile.DisplayName}). Instale o jogo pela Steam e tente detectar novamente.");
        if (!DetectedGame.IsExecutablePresent || !File.Exists(DetectedGame.ExecutablePath))
            throw new InvalidOperationException("Executável ausente. Verifique os arquivos do jogo na Steam.");
        PatchApplied = patches.IsPatchApplied(DetectedGame.InstallDirectory);
        Log($"{profile.DisplayName} detectado: {DetectedGame.InstallDirectory}");
    });

    [RelayCommand]
    private Task InstallAndActivatePatchAsync() => RunAsync(async () =>
    {
        RequireLicense();
        var profile = SelectedGame;
        var game = DetectedGame ?? throw new InvalidOperationException("Jogo não detectado. Execute Detectar jogo primeiro.");
        if (!game.IsExecutablePresent || !File.Exists(game.ExecutablePath))
            throw new InvalidOperationException("Executável ausente; instalação interrompida.");
        if (patches.IsPatchApplied(game.InstallDirectory))
            throw new InvalidOperationException("Patch já aplicado. Faça rollback antes de reinstalar.");
        if (!Directory.Exists(StagedFilesDirectory) || !Directory.EnumerateFiles(StagedFilesDirectory, "*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Selecione uma pasta com os arquivos do patch preparados. Download automático indisponível: perfil sem hashes de produção.");
        await patches.ApplyPatchAsync(game.InstallDirectory, StagedFilesDirectory, profile.GameId);
        PatchApplied = true;
        Log($"Patch ativado para {profile.DisplayName}; backup original preservado.");
    });

    [RelayCommand]
    private Task RollbackPatchAsync() => RunAsync(async () =>
    {
        var game = DetectedGame ?? throw new InvalidOperationException("Jogo não detectado. Execute Detectar jogo primeiro.");
        await bridge.StopAsync();
        RefreshTelemetry();
        if (!await patches.RollbackAsync(game.InstallDirectory))
            throw new InvalidOperationException("Nenhum patch aplicado para restaurar.");
        PatchApplied = false;
        Log("Rollback concluído; arquivos originais restaurados.");
    });

    [RelayCommand]
    private Task StartBridgeAsync() => RunAsync(async () =>
    {
        RequireLicense();
        await bridge.StartAsync();
        RefreshTelemetry();
        Log($"Bridge escutando em loopback (127.0.0.1:{BridgePort}) para {SelectedGame.DisplayName}; modo offline com fallback para texto original em caso de cache miss.");
    });

    [RelayCommand]
    private Task StopBridgeAsync() => RunAsync(async () =>
    {
        await bridge.StopAsync();
        RefreshTelemetry();
        Log("Bridge parada.");
    });

    public void RefreshTelemetry()
    {
        var s = bridge.GetSnapshot();
        BridgeStatus = bridge.IsRunning ? "Escutando • loopback (127.0.0.1)" : "Parada";
        BridgePort = bridge.Port;
        TokenSummary = bridge.TokenSummary;
        L0Hits = s.L0Hits;
        L1Hits = s.L1Hits;
        L2Hits = s.L2Hits;
        CircuitState = s.CircuitState;
        // L2 hits are also counted as local misses by the orchestrator.
        var total = s.L0Hits + s.L1Hits + s.Misses;
        CacheHitPercent = total == 0 ? 0 : 100d * (s.L0Hits + s.L1Hits + s.L2Hits) / total;
        if (s.FallbackCount > lastFallbacks)
            Log($"Fallback: {s.FallbackCount} respostas preservaram o texto original (offline ou falha de validação).");
        lastFallbacks = s.FallbackCount;
        OnPropertyChanged(nameof(CurrentStepHint));
    }
}
