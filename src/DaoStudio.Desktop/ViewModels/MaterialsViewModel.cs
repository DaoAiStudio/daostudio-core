using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaoStudio.Contracts;
using DaoStudio.Desktop.Services;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Desktop.ViewModels;

public partial class MaterialsViewModel : ObservableObject
{
    private readonly IGlossaryMatcher defaultGlossary;
    private readonly ITagMasker masker;
    private readonly IStringNormalizer normalizer;
    private readonly ITemplateHashCalculator hasher;
    private readonly ITranslationCache cache;
    private readonly DesktopResources resources;
    private IGlossaryMatcher activeGlossary;
    private bool selecting;
    private bool refreshingList;

    public IReadOnlyList<GameProfileManifest> AvailableGames { get; }
    public IReadOnlyList<string> Categories { get; } = new[] { "Manhua", "Webnovel", "LoreWiki" };

    [ObservableProperty] private GameProfileManifest selectedGame;
    [ObservableProperty] private string selectedCategory = "Manhua";
    [ObservableProperty] private MaterialBlockItem? selectedBlock;
    [ObservableProperty] private string translationText = "";
    [ObservableProperty] private string rawInputText = "";
    [ObservableProperty] private int defaultBubbleMaxChars = 55;
    [ObservableProperty] private string validationMessage = "";
    [ObservableProperty] private string statusMessage = "Pronto para revisar blocos de mangás/manhuas, capítulos de novels ou guias de lore.";
    [ObservableProperty] private string exportedPreview = "";

    public ObservableCollection<MaterialBlockItem> AllBlocks { get; } = new();
    public ObservableCollection<MaterialBlockItem> FilteredBlocks { get; } = new();
    public ObservableCollection<GlossaryEntry> DetectedTerms { get; } = new();

    public bool IsManhuaSelected => string.Equals(SelectedCategory, "Manhua", StringComparison.OrdinalIgnoreCase);
    public bool IsWebnovelSelected => string.Equals(SelectedCategory, "Webnovel", StringComparison.OrdinalIgnoreCase);
    public bool IsLoreWikiSelected => string.Equals(SelectedCategory, "LoreWiki", StringComparison.OrdinalIgnoreCase);

    public int CurrentCharCount => TranslationText.Length;
    public int CurrentMaxChars => SelectedBlock?.MaxChars ?? 0;
    public bool IsOverCharBudget => CurrentMaxChars > 0 && CurrentCharCount > CurrentMaxChars;

    public string CharBudgetSummary => SelectedBlock is null
        ? "Nenhum bloco selecionado"
        : CurrentMaxChars > 0
            ? $"{CurrentCharCount} / {CurrentMaxChars} chars ({SelectedBlock.BlockType})"
            : $"{CurrentCharCount} chars ({SelectedBlock.BlockType})";

    public string ProgressSummary => $"{FilteredBlocks.Count(b => b.Approved)} de {FilteredBlocks.Count} blocos aprovados em {SelectedCategory}";

    public MaterialsViewModel(IGlossaryMatcher glossary, ITagMasker masker, IStringNormalizer normalizer,
        ITemplateHashCalculator hasher, ITranslationCache cache, DesktopResources resources)
    {
        defaultGlossary = glossary;
        activeGlossary = glossary;
        this.masker = masker;
        this.normalizer = normalizer;
        this.hasher = hasher;
        this.cache = cache;
        this.resources = resources;
        AvailableGames = resources.LoadAllProfiles();
        selectedGame = AvailableGames[0];
        LoadSampleMaterials();
    }

    partial void OnSelectedGameChanged(GameProfileManifest value)
    {
        activeGlossary = string.Equals(value.GameId, "guigubahuang", StringComparison.OrdinalIgnoreCase)
            ? defaultGlossary
            : resources.LoadGlossary(value.GameId, defaultGlossary);

        RefreshDetectedTerms();
        StatusMessage = $"Glossário do universo atualizado para: {value.DisplayName}";
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsManhuaSelected));
        OnPropertyChanged(nameof(IsWebnovelSelected));
        OnPropertyChanged(nameof(IsLoreWikiSelected));
        RefreshFilteredBlocks(selectFirstPending: true);
    }

    partial void OnSelectedBlockChanged(MaterialBlockItem? value)
    {
        if (refreshingList) return;
        selecting = true;
        TranslationText = value?.TranslatedText ?? "";
        selecting = false;
        ValidationMessage = "";
        RefreshDetectedTerms();
        NotifyBudgetProperties();
    }

    partial void OnTranslationTextChanged(string value)
    {
        if (!selecting && SelectedBlock is { } block)
        {
            var index = AllBlocks.IndexOf(block);
            if (index >= 0)
            {
                var updated = block with { TranslatedText = value, Approved = false };
                AllBlocks[index] = updated;
                refreshingList = true;
                try
                {
                    var fIndex = FilteredBlocks.IndexOf(block);
                    if (fIndex >= 0) FilteredBlocks[fIndex] = updated;
                    SelectedBlock = updated;
                }
                finally { refreshingList = false; }
                OnPropertyChanged(nameof(ProgressSummary));
            }
        }
        NotifyBudgetProperties();
        CheckValidationPreview();
    }

    private void NotifyBudgetProperties()
    {
        OnPropertyChanged(nameof(CurrentCharCount));
        OnPropertyChanged(nameof(CurrentMaxChars));
        OnPropertyChanged(nameof(IsOverCharBudget));
        OnPropertyChanged(nameof(CharBudgetSummary));
    }

    private void RefreshDetectedTerms()
    {
        DetectedTerms.Clear();
        if (SelectedBlock is not null)
        {
            foreach (var entry in activeGlossary.FindMatches(SelectedBlock.SourceText))
                DetectedTerms.Add(entry);
        }
        CheckValidationPreview();
    }

    private void CheckValidationPreview()
    {
        if (SelectedBlock is null)
        {
            ValidationMessage = "";
            return;
        }

        var glossaryCheck = activeGlossary.ValidateTranslation(
            SelectedBlock.SourceText,
            TranslationText,
            "pt-BR",
            SelectedBlock.MaxChars > 0 ? SelectedBlock.MaxChars : null);

        var warnings = new List<string>();
        if (glossaryCheck.ForbiddenHits.Count > 0)
            warnings.Add("Termos proibidos: " + string.Join(", ", glossaryCheck.ForbiddenHits) + ".");
        if (IsOverCharBudget)
            warnings.Add($"Excede limite de typesetting ({CurrentCharCount}/{CurrentMaxChars} chars).");

        ValidationMessage = string.Join(" ", warnings);
    }

    private void RefreshFilteredBlocks(bool selectFirstPending = false)
    {
        var current = SelectedBlock;
        var matching = AllBlocks.Where(b => string.Equals(b.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase)).ToList();

        refreshingList = true;
        try
        {
            FilteredBlocks.Clear();
            foreach (var item in matching) FilteredBlocks.Add(item);
        }
        finally { refreshingList = false; }

        if (selectFirstPending || current is null || !FilteredBlocks.Contains(current))
            SelectedBlock = FilteredBlocks.FirstOrDefault(b => !b.Approved) ?? FilteredBlocks.FirstOrDefault();
        else
            SelectedBlock = current;

        OnPropertyChanged(nameof(ProgressSummary));
    }

    [RelayCommand]
    private void SelectCategory(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category) && Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
            SelectedCategory = Categories.First(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void LoadSampleMaterials()
    {
        AllBlocks.Clear();
        foreach (var item in resources.LoadMaterialSamples())
            AllBlocks.Add(item);

        RefreshFilteredBlocks(selectFirstPending: true);
        StatusMessage = $"Exemplos carregados ({AllBlocks.Count} blocos no total).";
    }

    [RelayCommand]
    private void SplitRawText()
    {
        ValidationMessage = "";
        var lines = RawInputText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            ValidationMessage = "Cole um trecho ou linhas em chinês antes de dividir em blocos.";
            return;
        }

        var budget = IsManhuaSelected ? Math.Max(15, DefaultBubbleMaxChars) : IsLoreWikiSelected ? 220 : 280;
        var blockType = IsManhuaSelected ? "Diálogo" : IsLoreWikiSelected ? "Enciclopédia" : "Narração";
        var prefix = IsManhuaSelected ? "Pág. Nova • Balão" : IsLoreWikiSelected ? "Entrada" : "Cap. Novo • Parágrafo";

        var created = new List<MaterialBlockItem>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var num = i + 1;
            var item = new MaterialBlockItem(
                $"custom-{Guid.NewGuid():N}",
                SelectedCategory,
                $"{prefix} {num:00}",
                blockType,
                lines[i],
                "",
                budget,
                false);
            AllBlocks.Add(item);
            created.Add(item);
        }

        RawInputText = "";
        RefreshFilteredBlocks(selectFirstPending: false);
        SelectedBlock = created[0];
        StatusMessage = $"{created.Count} novos blocos criados em {SelectedCategory} (limite {budget} chars).";
    }

    [RelayCommand]
    private void InsertTerm(GlossaryEntry? entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.PtBr)) return;
        TranslationText = string.IsNullOrEmpty(TranslationText) || TranslationText.EndsWith(' ')
            ? TranslationText + entry.PtBr
            : TranslationText + " " + entry.PtBr;
    }

    private string ValidateAndMaskPlaceholders(string source, string translated)
    {
        var original = masker.Mask(source);
        var target = masker.Mask(translated);
        var expected = original.Placeholders.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => new Queue<string>(g.Select(p => p.Key)), StringComparer.Ordinal);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in target.Placeholders)
        {
            if (!expected.TryGetValue(pair.Value, out var tokens) || tokens.Count == 0)
                throw new InvalidOperationException("Variável/tag alterada ou duplicada: " + pair.Value);
            mapping[pair.Key] = tokens.Dequeue();
        }
        var missing = expected.FirstOrDefault(p => p.Value.Count > 0);
        if (missing.Key is not null) throw new InvalidOperationException("Variável/tag ausente: " + missing.Key);
        var template = Regex.Replace(target.MaskedTemplate, @"\{\{(?:VAR|TAG|NUM)_[0-9]+\}\}", m => mapping[m.Value]);
        if (!masker.ValidateMaskIntegrity(original.MaskedTemplate, template, original.Placeholders, out var error))
            throw new InvalidOperationException("Integridade de marcadores inválida: " + error);
        return template;
    }

    [RelayCommand]
    private async Task ApproveBlockAsync()
    {
        ValidationMessage = "";
        var block = SelectedBlock;
        if (block is null)
        {
            ValidationMessage = "Selecione um bloco para aprovar.";
            return;
        }

        var translation = TranslationText;
        try
        {
            if (string.IsNullOrWhiteSpace(translation))
                throw new InvalidOperationException("Preencha a tradução antes de aprovar.");

            if (block.MaxChars > 0 && translation.Length > block.MaxChars)
                throw new InvalidOperationException($"Orçamento de typesetting excedido: {translation.Length}/{block.MaxChars} caracteres.");

            var glossaryCheck = activeGlossary.ValidateTranslation(block.SourceText, translation, "pt-BR", block.MaxChars > 0 ? block.MaxChars : null);
            if (glossaryCheck.ForbiddenHits.Count > 0)
                throw new InvalidOperationException("Termos proibidos encontrados: " + string.Join(", ", glossaryCheck.ForbiddenHits));

            var template = ValidateAndMaskPlaceholders(block.SourceText, translation);
            var key = new CacheKey(
                SelectedGame.GameId,
                "pt-BR",
                hasher.ComputeHash(normalizer.Normalize(masker.Mask(block.SourceText).MaskedTemplate)),
                SelectedGame.GlossaryVersion);
            await cache.SetAsync(key, template);

            var index = AllBlocks.IndexOf(block);
            if (index < 0) return;
            var fIndex = Math.Max(0, FilteredBlocks.IndexOf(block));
            var approved = block with { TranslatedText = translation, Approved = true };
            AllBlocks[index] = approved;

            RefreshFilteredBlocks(selectFirstPending: false);
            var nextPending = FilteredBlocks.Skip(fIndex + 1).Concat(FilteredBlocks.Take(fIndex)).FirstOrDefault(b => !b.Approved) ?? approved;
            SelectedBlock = nextPending;
            StatusMessage = $"Bloco {block.Reference} aprovado e gravado no cache L0/L1 ({SelectedGame.GameId}).";
        }
        catch (Exception ex)
        {
            ValidationMessage = "Aprovação bloqueada: " + ex.Message;
        }
    }

    [RelayCommand]
    private void GenerateBilingualExport()
    {
        var blocks = FilteredBlocks.ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# DaoStudio — Exportação ({SelectedCategory} • {SelectedGame.DisplayName})");
        sb.AppendLine();

        foreach (var b in blocks)
        {
            if (IsManhuaSelected)
            {
                var status = b.Approved ? "APROVADO" : "RASCUNHO";
                sb.AppendLine($"## [{b.Reference}] ({b.BlockType} • {b.TranslatedText.Length}/{b.MaxChars} chars • {status})");
                sb.AppendLine($"- ZH: {b.SourceText}");
                sb.AppendLine($"- PT: {b.TranslatedText}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"### {b.Reference}");
                sb.AppendLine($"> [ZH] {b.SourceText}");
                sb.AppendLine();
                sb.AppendLine(string.IsNullOrWhiteSpace(b.TranslatedText) ? "_(Pendente)_" : b.TranslatedText);
                sb.AppendLine();
            }
        }

        ExportedPreview = sb.ToString();
        StatusMessage = $"Prévia bilíngue gerada para {blocks.Count} blocos de {SelectedCategory}.";
    }

    [RelayCommand]
    private async Task ExportToFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            GenerateBilingualExport();
            return;
        }

        if (string.IsNullOrWhiteSpace(ExportedPreview))
            GenerateBilingualExport();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, ExportedPreview, Encoding.UTF8);
        StatusMessage = "Exportado para: " + path;
    }
}
