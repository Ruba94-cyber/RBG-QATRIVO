using Avalonia;
using Avalonia.Styling;

namespace TestLoom.App.Services;

/// <summary>Preferenza visiva per PC: non modifica workspace, JSON condivisi o percorsi dell'app.</summary>
public static class ThemeAppearanceService
{
    public static ThemeVariant FromPreference(string? preference) =>
        preference?.Trim().ToLowerInvariant() switch
        {
            "dark" => ThemeVariant.Dark,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Light
        };

    public static void Apply(string? preference)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = FromPreference(preference);
    }
}
