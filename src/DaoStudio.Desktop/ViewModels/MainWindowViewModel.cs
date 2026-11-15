using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaoStudio.Contracts;

namespace DaoStudio.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private bool syncingGame;

    public LauncherViewModel Launcher { get; }
    public WorkbenchViewModel Workbench { get; }
    public MaterialsViewModel Materials { get; }
    public TourViewModel Tour { get; }
    public IReadOnlyList<GameProfileManifest> AvailableGames => Launcher.AvailableGames;

    [ObservableProperty] private ObservableObject currentView;
    [ObservableProperty] private GameProfileManifest selectedGame;

    public bool IsTourActive => CurrentView == Tour;
    public bool IsSidebarVisible => !IsTourActive;
    public bool IsLauncherActive => CurrentView == Launcher;
    public bool IsWorkbenchActive => CurrentView == Workbench;
    public bool IsMaterialsActive => CurrentView == Materials;

    public MainWindowViewModel(LauncherViewModel launcher, WorkbenchViewModel workbench, MaterialsViewModel materials, TourViewModel tour)
    {
        Launcher = launcher;
        Workbench = workbench;
        Materials = materials;
        Tour = tour;
        currentView = tour;
        selectedGame = launcher.SelectedGame;

        Tour.NavigateToModuleRequested += target => CurrentView = target;

        Launcher.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LauncherViewModel.SelectedGame))
                SyncSelectedGame(Launcher.SelectedGame);
        };
        Workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.SelectedGame))
                SyncSelectedGame(Workbench.SelectedGame);
        };
        Materials.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaterialsViewModel.SelectedGame))
                SyncSelectedGame(Materials.SelectedGame);
        };
        Tour.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TourViewModel.SelectedGame))
                SyncSelectedGame(Tour.SelectedGame);
        };
    }

    partial void OnCurrentViewChanged(ObservableObject value)
    {
        OnPropertyChanged(nameof(IsTourActive));
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(IsLauncherActive));
        OnPropertyChanged(nameof(IsWorkbenchActive));
        OnPropertyChanged(nameof(IsMaterialsActive));
    }

    partial void OnSelectedGameChanged(GameProfileManifest value) => SyncSelectedGame(value);

    private void SyncSelectedGame(GameProfileManifest game)
    {
        if (syncingGame || game is null) return;
        syncingGame = true;
        try
        {
            SelectedGame = game;
            Launcher.SelectedGame = game;
            Workbench.SelectedGame = game;
            Materials.SelectedGame = game;
            Tour.SelectedGame = game;
        }
        finally { syncingGame = false; }
    }

    [RelayCommand] private void ShowTour() => CurrentView = Tour;
    [RelayCommand] private void ShowLauncher() => CurrentView = Launcher;
    [RelayCommand] private void ShowWorkbench() => CurrentView = Workbench;
    [RelayCommand] private void ShowMaterials() => CurrentView = Materials;
}
