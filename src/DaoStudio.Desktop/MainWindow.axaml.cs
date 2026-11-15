using Avalonia.Controls;
using Avalonia.Threading;
using DaoStudio.Desktop.ViewModels;

namespace DaoStudio.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer telemetryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public MainWindow()
    {
        InitializeComponent();
        telemetryTimer.Tick += (_, _) => (DataContext as MainWindowViewModel)?.Launcher.RefreshTelemetry();
        Opened += async (_, _) =>
        {
            telemetryTimer.Start();
            if (DataContext is MainWindowViewModel model) await model.Launcher.DetectGameCommand.ExecuteAsync(null);
        };
        Closed += (_, _) => telemetryTimer.Stop();
    }
}
