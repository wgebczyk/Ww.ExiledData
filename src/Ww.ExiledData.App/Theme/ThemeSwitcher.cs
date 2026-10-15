using Avalonia;
using Avalonia.Styling;

namespace Ww.ExiledData.App.Theme;

/// <summary>Applies the Theme setting to the whole app; every open window re-resolves its token brushes.</summary>
public static class ThemeSwitcher
{
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = ToVariant(theme); // Default follows the OS, live
    }

    public static ThemeVariant ToVariant(AppTheme theme) => theme switch
    {
        AppTheme.Dark => ThemeVariant.Dark,
        AppTheme.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    /// <summary>The theme a quick toggle switches to: the opposite of what is shown now.</summary>
    public static AppTheme Toggled() =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Light ? AppTheme.Dark : AppTheme.Light;
}
