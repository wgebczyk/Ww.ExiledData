using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Ww.ExiledData.MockGame;

/// <summary>
/// A stand-in for the game window: an inventory grid at the calibrated position and a map device, both with
/// randomizable content. Ctrl+C over an item puts its text on the clipboard, like the game does.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<MockApp>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

internal sealed class MockApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MockGameWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
