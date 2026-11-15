using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaoStudio.Contracts;
using DaoStudio.Desktop.Services;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Desktop.ViewModels;

public partial class WorkbenchViewModel : ObservableObject
{
    private readonly ILocalizationFileParser[] parsers;
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
    [ObservableProperty] private GameProfileManifest selectedGame;
    [ObservableProperty] private LocalizationSegment? selectedSegment;
    [ObservableProperty] private string translationText = "";
    [ObservableProperty] private string validationError = "";
    [ObservableProperty] private string glossaryWarnings = "";
    [ObservableProperty] private string filePath = "";
    [ObservableProperty] private string fileStatus = "Importe JSON, CSV ou TXT, ou carregue as amostras do jogo selecionado.";
    [ObservableProperty] private string searchQuery = "";
    [ObservableProperty] private string statusFilter = "Todos";

    public ObservableCollection<LocalizationSegment> Segments { get; } = new();
    public ObservableCollection<LocalizationSegment> FilteredSegments { get; } = new();
    public ObservableCollection<GlossaryEntry> CanonicalTerms { get; } = new();
    public ObservableCollection<string> DetectedPlaceholders { get; } = new();

    public int ApprovedCount => Segments.Count(s => s.Approved);
    public int PendingCount => Segments.Count - ApprovedCount;
    public double ProgressPercent => Segments.Count == 0 ? 0 : 100d * ApprovedCount / Segments.Count;
    public string Progress => $"{ApprovedCount} de {Segments.Count} aprovados";

    public WorkbenchViewModel(IEnumerable<ILocalizationFileParser> parsers, IGlossaryMatcher glossary, ITagMasker masker,
        IStringNormalizer normalizer, ITemplateHashCalculator hasher, ITranslationCache cache, DesktopResources resources)
    {
        this.parsers = parsers.ToArray();
        defaultGlossary = glossary;
        activeGlossary = glossary;
        this.masker = masker;
        this.normalizer = normalizer;
        this.hasher = hasher;
        this.cache = cache;
        this.resources = resources;
        AvailableGames = resources.LoadAllProfiles();
        selectedGame = AvailableGames[0];
    }

    partial void OnSelectedGameChanged(GameProfileManifest value)
    {
        activeGlossary = string.Equals(value.GameId, "guigubahuang", StringComparison.OrdinalIgnoreCase)
            ? defaultGlossary
            : resources.LoadGlossary(value.GameId, defaultGlossary);

        RefreshCanonicalTermsAndWarnings();
        FileStatus = $"Jogo ativo: {value.DisplayName}. Clique em 'Carregar amostras' ou importe um arquivo.";
    }

    partial void OnSearchQueryChanged(string value) => RefreshFilteredSegments();
    partial void OnStatusFilterChanged(string value) => RefreshFilteredSegments();

    partial void OnSelectedSegmentChanged(LocalizationSegment? value)
    {
        if (refreshingList) return;
        selecting = true;
        TranslationText = value?.TranslatedText ?? "";
        selecting = false;
        ValidationError = "";
        RefreshCanonicalTermsAndWarnings();
        DetectedPlaceholders.Clear();
        if (value is not null)
        {
            foreach (var ph in masker.Mask(value.SourceText).Placeholders.Values.Distinct(StringComparer.Ordinal))
                DetectedPlaceholders.Add(ph);
        }
    }

    partial void OnTranslationTextChanged(string value)
    {
        if (!selecting && SelectedSegment is { } segment)
        {
            var index = Segments.IndexOf(segment);
            if (index >= 0)
            {
                var updated = segment with { TranslatedText = value, Approved = false };
                Segments[index] = updated;
                refreshingList = true;
                try
                {
                    var filteredIndex = FilteredSegments.IndexOf(segment);
                    if (filteredIndex >= 0) FilteredSegments[filteredIndex] = updated;
                    SelectedSegment = updated;
                }
                finally { refreshingList = false; }
                NotifyProgressChanged();
            }
        }
        UpdateWarnings();
    }

    private void RefreshCanonicalTermsAndWarnings()
    {
        CanonicalTerms.Clear();
        if (SelectedSegment is not null)
        {
            foreach (var entry in activeGlossary.FindMatches(SelectedSegment.SourceText))
                CanonicalTerms.Add(entry);
        }
        UpdateWarnings();
    }

    private void NotifyProgressChanged()
    {
        OnPropertyChanged(nameof(ApprovedCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(Progress));
    }

    private void RefreshFilteredSegments()
    {
        var current = SelectedSegment;
        var query = SearchQuery.Trim();
        IEnumerable<LocalizationSegment> items = Segments;
        if (StatusFilter == "Pendentes") items = items.Where(s => !s.Approved);
        else if (StatusFilter == "Aprovados") items = items.Where(s => s.Approved);

        if (query.Length > 0)
        {
            items = items.Where(s =>
                s.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.SourceText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (s.TranslatedText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = items.ToList();
        refreshingList = true;
        try
        {
            FilteredSegments.Clear();
            foreach (var item in list) FilteredSegments.Add(item);
            if (current is not null && FilteredSegments.Contains(current))
                SelectedSegment = current;
        }
        finally { refreshingList = false; }
    }

    private void UpdateWarnings()
    {
        if (SelectedSegment is null) { GlossaryWarnings = ""; return; }
        var result = activeGlossary.ValidateTranslation(SelectedSegment.SourceText, TranslationText);
        GlossaryWarnings = string.Join(" ", new[] {
            result.ForbiddenHits.Count > 0 ? "Termos proibidos: " + string.Join(", ", result.ForbiddenHits) + "." : "",
            result.ExceedsMaxChars ? "Tradução excede maxChars do glossário." : "" }.Where(s => s.Length > 0));
    }

    private ILocalizationFileParser Parser(string path) => parsers.FirstOrDefault(p => p.CanParse(Path.GetExtension(path)))
        ?? throw new FormatException("Formato não suportado. Use JSON, CSV ou TXT.");

    public async Task ImportFileAsync(string path)
    {
        var parser = Parser(path);
        var content = await File.ReadAllTextAsync(path);
        var items = parser.Parse(content);
        // TXT has no metadata fields. A companion JSON preserves source and review state.
        if (parser.CanParse(".txt") && File.Exists(path + ".review.json"))
        {
            var reviewed = Parser(".json").Parse(await File.ReadAllTextAsync(path + ".review.json"));
            if (parser.Export(reviewed).Replace("\r\n", "\n") == content.Replace("\r\n", "\n")) items = reviewed;
        }
        SelectedSegment = null;
        Segments.Clear();
        foreach (var item in items) Segments.Add(item);
        RefreshFilteredSegments();
        SelectedSegment = Segments.FirstOrDefault(s => !s.Approved) ?? Segments.FirstOrDefault();
        NotifyProgressChanged();
        FileStatus = $"{Segments.Count} segmentos importados ({SelectedGame.DisplayName}).";
    }

    public async Task ExportFileAsync(string path)
    {
        var parser = Parser(path);
        var snapshot = Segments.ToArray();
        await File.WriteAllTextAsync(path, parser.Export(snapshot));
        if (parser.CanParse(".txt")) await File.WriteAllTextAsync(path + ".review.json", Parser(".json").Export(snapshot));
        FileStatus = parser.CanParse(".txt") ? "TXT exportado com aprovações no arquivo .review.json acompanhante." : "Exportado com aprovações preservadas.";
    }

    private async Task FileAction(Func<Task> action)
    {
        try { ValidationError = ""; await action(); }
        catch (Exception ex) { ValidationError = "Falha no arquivo: " + ex.Message; }
    }

    [RelayCommand] private Task ImportAsync() => FileAction(() => ImportFileAsync(FilePath));
    [RelayCommand] private Task ExportAsync() => FileAction(() => ExportFileAsync(FilePath));
    [RelayCommand] private Task LoadSeedAsync() => FileAction(() => ImportFileAsync(resources.GetSeedPath(SelectedGame.GameId)));
    [RelayCommand] private void SetFilter(string? filter) => StatusFilter = string.IsNullOrWhiteSpace(filter) ? "Todos" : filter;

    [RelayCommand]
    private void InsertPlaceholder(string? placeholder)
    {
        if (string.IsNullOrEmpty(placeholder)) return;
        TranslationText = string.IsNullOrEmpty(TranslationText) || TranslationText.EndsWith(' ')
            ? TranslationText + placeholder
            : TranslationText + " " + placeholder;
    }

    [RelayCommand]
    private void InsertGlossaryTerm(GlossaryEntry? entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.PtBr)) return;
        TranslationText = string.IsNullOrEmpty(TranslationText) || TranslationText.EndsWith(' ')
            ? TranslationText + entry.PtBr
            : TranslationText + " " + entry.PtBr;
    }

    private string ValidateAndMask(string source, string translated)
    {
        var original = masker.Mask(source);
        var target = masker.Mask(translated);
        var expected = original.Placeholders.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => new Queue<string>(g.Select(p => p.Key)), StringComparer.Ordinal);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in target.Placeholders)
        {
            if (!expected.TryGetValue(pair.Value, out var tokens) || tokens.Count == 0)
                throw new InvalidOperationException("Variável/tag inesperada, alterada ou duplicada: " + pair.Value);
            mapping[pair.Key] = tokens.Dequeue();
        }
        var missing = expected.FirstOrDefault(p => p.Value.Count > 0);
        if (missing.Key is not null) throw new InvalidOperationException("Variável/tag ausente: " + missing.Key);
        var template = Regex.Replace(target.MaskedTemplate, @"\{\{(?:VAR|TAG|NUM)_[0-9]+\}\}", m => mapping[m.Value]);
        if (!masker.ValidateMaskIntegrity(original.MaskedTemplate, template, original.Placeholders, out var error))
            throw new InvalidOperationException("Variáveis inválidas: " + error);
        return template;
    }

    [RelayCommand]
    private async Task ApproveSegmentAsync()
    {
        ValidationError = "";
        var segment = SelectedSegment;
        if (segment is null) { ValidationError = "Selecione um segmento para aprovar."; return; }
        var translation = TranslationText;
        try
        {
            if (string.IsNullOrWhiteSpace(translation)) throw new InvalidOperationException("Tradução vazia: preencha o texto antes de aprovar.");
            var template = ValidateAndMask(segment.SourceText, translation);
            UpdateWarnings();
            if (GlossaryWarnings.Length > 0) throw new InvalidOperationException(GlossaryWarnings);
            var key = new CacheKey(SelectedGame.GameId, "pt-BR", hasher.ComputeHash(normalizer.Normalize(masker.Mask(segment.SourceText).MaskedTemplate)), SelectedGame.GlossaryVersion);
            await cache.SetAsync(key, template);
            var index = Segments.IndexOf(segment);
            if (index < 0) return; // The user imported or edited while persistence was pending.
            var approved = segment with { TranslatedText = translation, Approved = true };
            Segments[index] = approved;
            var next = Segments.Skip(index + 1).Concat(Segments.Take(index)).FirstOrDefault(s => !s.Approved) ?? approved;
            RefreshFilteredSegments();
            NotifyProgressChanged();
            if (SelectedSegment == segment || SelectedSegment is null)
                SelectedSegment = next;
        }
        catch (Exception ex) { ValidationError = "Aprovação bloqueada: " + ex.Message; }
    }
}
