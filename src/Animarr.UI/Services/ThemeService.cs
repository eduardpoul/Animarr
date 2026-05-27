namespace Animarr.UI.Services;

/// <summary>
/// Three-value theme mode mirror — replaces the FluentUI one. Stored as a
/// string in AppConfig (<c>"System" | "Light" | "Dark"</c>); parsing /
/// formatting happens in the Settings page.
/// </summary>
public enum ThemeMode
{
    System = 0,
    Light  = 1,
    Dark   = 2,
}

/// <summary>
/// Singleton service that holds the current accent hue and theme mode.
/// Notifies subscribers (MainLayout pushes the hue into a CSS custom
/// property on document.documentElement; Settings page reflects the
/// active swatch).
///
/// Accent is hue-degree based per CANVAS §01.2. The five canonical presets
/// (crimson 25, amber 75, green 150, blue 240, violet 300) drive every
/// <c>oklch(L C var(--accent-hue))</c> rule throughout the stylesheet — a
/// single CSS variable swap rotates the entire palette without re-bundling.
/// </summary>
public sealed class ThemeService
{
    public const int DefaultHue = 25; // crimson

    public ThemeMode Mode { get; private set; } = ThemeMode.System;
    public int AccentHue { get; private set; } = DefaultHue;

    public event Action? OnChange;

    public void Set(ThemeMode mode)
    {
        Mode = mode;
        OnChange?.Invoke();
    }

    /// <summary>Sets accent by hue degree (0..360).</summary>
    public void SetAccentHue(int hue)
    {
        AccentHue = ((hue % 360) + 360) % 360;
        OnChange?.Invoke();
    }
}
