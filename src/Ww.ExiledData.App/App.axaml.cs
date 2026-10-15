using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Ww.ExiledData.App;

public partial class App : Application
{
    private AppHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost(desktop.Args ?? []);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose; // the overlay window must not keep the app alive
            desktop.MainWindow = _host.CreateMainWindow();
            desktop.Exit += (_, _) => _host.Dispose();
            _host.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
