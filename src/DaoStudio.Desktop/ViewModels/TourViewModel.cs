using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaoStudio.Contracts;

namespace DaoStudio.Desktop.ViewModels;

public partial class TourViewModel : ObservableObject
{
    private readonly LauncherViewModel launcher;
    private readonly WorkbenchViewModel workbench;
    private readonly MaterialsViewModel materials;

    public IReadOnlyList<GameProfileManifest> AvailableGames => launcher.AvailableGames;

    [ObservableProperty] private GameProfileManifest selectedGame;
    [ObservableProperty] private int currentStage;
    [ObservableProperty] private bool hasPickedIntent;
    [ObservableProperty] private bool hasPickedUniverse;
    [ObservableProperty] private bool hasPickedStyle;
    [ObservableProperty] private string selectedIntent = "player";
    [ObservableProperty] private string experienceLevel = "Guiado";
    [ObservableProperty] private bool autoLoadSamples = true;
    [ObservableProperty] private bool autoGenerateBilingualPreview = true;
    [ObservableProperty] private int customBubbleMaxChars = 55;
    [ObservableProperty] private string tourStatusMessage = "Responda às etapas ou escolha um workspace na lateral.";

    public bool IsStage0Intro => CurrentStage == 0;
    public bool IsStage1Intent => CurrentStage == 1;
    public bool IsStage2Universe => CurrentStage == 2;
    public bool IsStage3Style => CurrentStage == 3;
    public bool IsStage4Blueprint => CurrentStage == 4;

    public bool CanGoBack => CurrentStage > 0;
    public bool CanGoNext => CurrentStage < 4;
    public int WizardProgressPercent => CurrentStage * 25;
    public string StageCodeLabel => $"0{CurrentStage} / 04";

    public string StageStepLabel => CurrentStage switch
    {
        0 => "Configuração inicial",
        1 => "Objetivo principal",
        2 => "Universo e glossário",
        3 => "Preferências e escopo",
        _ => "Fluxo pronto"
    };

    public bool IsPlayerIntent => string.Equals(SelectedIntent, "player", StringComparison.OrdinalIgnoreCase);
    public bool IsCatIntent => string.Equals(SelectedIntent, "cat", StringComparison.OrdinalIgnoreCase);
    public bool IsManhuaIntent => string.Equals(SelectedIntent, "manhua", StringComparison.OrdinalIgnoreCase);
    public bool IsWebnovelIntent => string.Equals(SelectedIntent, "webnovel", StringComparison.OrdinalIgnoreCase);

    public bool IsGuidedLevel => string.Equals(ExperienceLevel, "Guiado", StringComparison.OrdinalIgnoreCase);
    public bool IsAdvancedLevel => string.Equals(ExperienceLevel, "Avancado", StringComparison.OrdinalIgnoreCase);

    public bool IsBubbleCompact => CustomBubbleMaxChars <= 45;
    public bool IsBubbleStandard => CustomBubbleMaxChars is > 45 and <= 62;
    public bool IsBubbleWide => CustomBubbleMaxChars > 62;

    public string Question2Title => IsManhuaIntent || IsWebnovelIntent
        ? "Qual universo Xianxia/Wuxia serve de base para o glossário da obra?"
        : "Em qual jogo você quer iniciar sua tradução hoje?";

    public string Question2Subtitle => IsManhuaIntent || IsWebnovelIntent
        ? "Reinos de cultivo, técnicas marciais e títulos do universo selecionado calibram automaticamente o glossário PT-BR."
        : "Cada perfil já traz engine, loader e dicionário base prontos (inclusive em modo debug sem licença).";

    public string Question3Title => SelectedIntent.ToLowerInvariant() switch
    {
        "manhua" => "Como você quer calibrar balões e revisão no Scanlation?",
        "webnovel" => "Qual nível de precisão literária você quer nos capítulos?",
        "cat" => "Como você prefere operar no Studio CAT?",
        _ => "Qual nível de automação você prefere no In-Game Translate?"
    };

    public bool IsNode1Active => HasPickedIntent || CurrentStage >= 2;
    public bool IsNode2Active => HasPickedUniverse || CurrentStage >= 3;
    public bool IsNode3Active => HasPickedStyle || CurrentStage >= 4;
    public bool IsNode4Active => CurrentStage >= 4 || (IsNode1Active && IsNode2Active && IsNode3Active);

    public string PipelineNode1Text => IsNode1Active ? ShortIntentLabel : "Objetivo";
    public string PipelineNode2Text => IsNode2Active ? SelectedGame.DisplayName : "Universo";
    public string PipelineNode3Text => IsNode3Active ? StyleSummaryLabel : "Preferências";
    public string PipelineNode4Text => IsNode4Active ? RecommendedModuleName : "Workspace";

    private string ShortIntentLabel => SelectedIntent.ToLowerInvariant() switch
    {
        "cat" => "Studio CAT",
        "manhua" => "Scan Manhua",
        "webnovel" => "Webnovel & Lore",
        _ => "In-Game 1-Click"
    };

    private string ExperienceLevelLabel => IsAdvancedLevel ? "Avançado" : "Guiado";

    private string StyleSummaryLabel => IsManhuaIntent
        ? $"{ExperienceLevelLabel} · {Math.Max(15, CustomBubbleMaxChars)}ch"
        : $"{ExperienceLevelLabel}{(AutoLoadSamples ? " + Exemplos" : "")}";

    public string InferredPersonaBadge => SelectedIntent.ToLowerInvariant() switch
    {
        "cat" => "Modder & Studio CAT",
        "manhua" => "Scanlation Manhua",
        "webnovel" => "Webnovels & Lore",
        _ => "In-Game 1-Click"
    };

    public string RecommendedModuleName => SelectedIntent.ToLowerInvariant() switch
    {
        "cat" => "Studio CAT",
        "manhua" => "Novels & Mangás · Manhua",
        "webnovel" => "Novels & Mangás · Webnovel",
        _ => "In-Game Bridge"
    };

    public string InferredExperienceDescription
    {
        get
        {
            var modeNote = IsGuidedLevel
                ? "Modo Guiado com dicas passo a passo e glossário automático ativo."
                : "Modo Avançado focado em produtividade direta e atalhos.";
            var sampleNote = AutoLoadSamples
                ? "As amostras e termos canônicos já serão pré-carregados."
                : "O workspace abrirá limpo para seus próprios arquivos.";

            return SelectedIntent.ToLowerInvariant() switch
            {
                "cat" => $"Abrirá o Studio Workbench (CAT) configurado para {SelectedGame.DisplayName} ({SelectedGame.Engine}), com trava de variáveis {{PlayerName}}/{{0}} e validação terminológica. {modeNote} {sampleNote}",
                "manhua" => $"Abrirá o módulo de Materiais na aba Mangá/Manhua com orçamento padrão de {Math.Max(15, CustomBubbleMaxChars)} caracteres por balão, glossário de {SelectedGame.DisplayName} e roteiro para typesetters. {modeNote} {sampleNote}",
                "webnovel" => $"Abrirá o módulo de Materiais na aba Webnovel com divisor de capítulos em chinês, glossário Xianxia/Wuxia ({SelectedGame.DisplayName}) e exportação bilíngue ZH + PT-BR. {modeNote} {sampleNote}",
                _ => $"Abrirá o In-Game Bridge configurado para {SelectedGame.DisplayName} (AppID {SelectedGame.SteamAppId}, loader {SelectedGame.Loader.Type}), pronto para detecção na Steam, Bridge loopback e cache L0/L1 em modo debug. {modeNote}"
            };
        }
    }

    public string PrimaryActionButtonText => $"Abrir {RecommendedModuleName}";

    public event Action<ObservableObject>? NavigateToModuleRequested;

    public TourViewModel(LauncherViewModel launcher, WorkbenchViewModel workbench, MaterialsViewModel materials)
    {
        this.launcher = launcher;
        this.workbench = workbench;
        this.materials = materials;
        selectedGame = launcher.SelectedGame;
    }

    partial void OnCurrentStageChanged(int value) => NotifyInferredProperties();
    partial void OnHasPickedIntentChanged(bool value) => NotifyInferredProperties();
    partial void OnHasPickedUniverseChanged(bool value) => NotifyInferredProperties();
    partial void OnHasPickedStyleChanged(bool value) => NotifyInferredProperties();
    partial void OnSelectedGameChanged(GameProfileManifest value) => NotifyInferredProperties();

    partial void OnSelectedIntentChanged(string value)
    {
        OnPropertyChanged(nameof(IsPlayerIntent));
        OnPropertyChanged(nameof(IsCatIntent));
        OnPropertyChanged(nameof(IsManhuaIntent));
        OnPropertyChanged(nameof(IsWebnovelIntent));
        NotifyInferredProperties();
    }

    partial void OnExperienceLevelChanged(string value)
    {
        OnPropertyChanged(nameof(IsGuidedLevel));
        OnPropertyChanged(nameof(IsAdvancedLevel));
        NotifyInferredProperties();
    }

    partial void OnAutoLoadSamplesChanged(bool value) => NotifyInferredProperties();
    partial void OnAutoGenerateBilingualPreviewChanged(bool value) => NotifyInferredProperties();

    partial void OnCustomBubbleMaxCharsChanged(int value)
    {
        OnPropertyChanged(nameof(IsBubbleCompact));
        OnPropertyChanged(nameof(IsBubbleStandard));
        OnPropertyChanged(nameof(IsBubbleWide));
        NotifyInferredProperties();
    }

    private void NotifyInferredProperties()
    {
        OnPropertyChanged(nameof(IsStage0Intro));
        OnPropertyChanged(nameof(IsStage1Intent));
        OnPropertyChanged(nameof(IsStage2Universe));
        OnPropertyChanged(nameof(IsStage3Style));
        OnPropertyChanged(nameof(IsStage4Blueprint));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(WizardProgressPercent));
        OnPropertyChanged(nameof(StageCodeLabel));
        OnPropertyChanged(nameof(StageStepLabel));
        OnPropertyChanged(nameof(Question2Title));
        OnPropertyChanged(nameof(Question2Subtitle));
        OnPropertyChanged(nameof(Question3Title));
        OnPropertyChanged(nameof(IsNode1Active));
        OnPropertyChanged(nameof(IsNode2Active));
        OnPropertyChanged(nameof(IsNode3Active));
        OnPropertyChanged(nameof(IsNode4Active));
        OnPropertyChanged(nameof(PipelineNode1Text));
        OnPropertyChanged(nameof(PipelineNode2Text));
        OnPropertyChanged(nameof(PipelineNode3Text));
        OnPropertyChanged(nameof(PipelineNode4Text));
        OnPropertyChanged(nameof(InferredPersonaBadge));
        OnPropertyChanged(nameof(RecommendedModuleName));
        OnPropertyChanged(nameof(InferredExperienceDescription));
        OnPropertyChanged(nameof(PrimaryActionButtonText));
    }

    [RelayCommand]
    private void StartWizard()
    {
        CurrentStage = 1;
    }

    [RelayCommand]
    private void NextStage()
    {
        if (CurrentStage < 4)
            CurrentStage++;
    }

    [RelayCommand]
    private void PreviousStage()
    {
        if (CurrentStage > 0)
            CurrentStage--;
    }

    [RelayCommand]
    private void JumpToStage(string? stageText)
    {
        if (int.TryParse(stageText, out var stage))
            CurrentStage = Math.Clamp(stage, 0, 4);
    }

    [RelayCommand]
    private void SelectIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return;

        SelectedIntent = intent.Trim().ToLowerInvariant();
        HasPickedIntent = true;
        if (CurrentStage <= 1)
            CurrentStage = 2;
    }

    [RelayCommand]
    private void SelectUniverse(GameProfileManifest? game)
    {
        if (game is null)
            return;

        SelectedGame = game;
        HasPickedUniverse = true;
        if (CurrentStage <= 2)
            CurrentStage = 3;
    }

    [RelayCommand]
    private void SelectBubblePreset(string? preset)
    {
        if (int.TryParse(preset, out var maxChars))
            CustomBubbleMaxChars = Math.Clamp(maxChars, 15, 140);
    }

    [RelayCommand]
    private void SelectExperienceLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return;

        ExperienceLevel = level;
        HasPickedStyle = true;
    }

    [RelayCommand]
    private void ChooseStyleAndAdvance(string? level)
    {
        if (!string.IsNullOrWhiteSpace(level))
            ExperienceLevel = level;

        HasPickedStyle = true;
        CurrentStage = 4;
    }

    [RelayCommand]
    private async Task ApplyAndStartAsync()
    {
        launcher.SelectedGame = SelectedGame;
        workbench.SelectedGame = SelectedGame;
        materials.SelectedGame = SelectedGame;

        switch (SelectedIntent.ToLowerInvariant())
        {
            case "cat":
                if (AutoLoadSamples)
                    await workbench.LoadSeedCommand.ExecuteAsync(null);
                TourStatusMessage = $"Experiência CAT configurada para {SelectedGame.DisplayName}.";
                NavigateToModuleRequested?.Invoke(workbench);
                break;

            case "manhua":
                materials.DefaultBubbleMaxChars = Math.Max(15, CustomBubbleMaxChars);
                materials.SelectCategoryCommand.Execute("Manhua");
                if (AutoLoadSamples)
                    materials.LoadSampleMaterialsCommand.Execute(null);
                if (AutoGenerateBilingualPreview)
                    materials.GenerateBilingualExportCommand.Execute(null);
                TourStatusMessage = "Experiência Scanlation Manhua aplicada.";
                NavigateToModuleRequested?.Invoke(materials);
                break;

            case "webnovel":
                materials.SelectCategoryCommand.Execute("Webnovel");
                if (AutoLoadSamples)
                    materials.LoadSampleMaterialsCommand.Execute(null);
                if (AutoGenerateBilingualPreview)
                    materials.GenerateBilingualExportCommand.Execute(null);
                TourStatusMessage = "Experiência Webnovel & Lore aplicada.";
                NavigateToModuleRequested?.Invoke(materials);
                break;

            default:
                TourStatusMessage = $"Experiência Player 1-Click configurada para {SelectedGame.DisplayName}.";
                NavigateToModuleRequested?.Invoke(launcher);
                break;
        }
    }

    [RelayCommand]
    private void ResetTour()
    {
        CurrentStage = 0;
        HasPickedIntent = false;
        HasPickedUniverse = false;
        HasPickedStyle = false;
        SelectedIntent = "player";
        SelectedGame = launcher.SelectedGame;
        ExperienceLevel = "Guiado";
        AutoLoadSamples = true;
        AutoGenerateBilingualPreview = true;
        CustomBubbleMaxChars = 55;
        TourStatusMessage = "Fluxo reiniciado para a configuração inicial.";
    }
}
