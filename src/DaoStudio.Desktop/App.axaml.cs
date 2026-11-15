using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DaoStudio.Desktop.DependencyInjection;
using DaoStudio.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DaoStudio.Desktop;

public partial class App : Application
{
    private ServiceProvider? services;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            services = new ServiceCollection().AddDaoStudioDesktop().BuildServiceProvider();
            desktop.MainWindow = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            desktop.Exit += (_, _) => services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
