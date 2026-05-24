using Microsoft.FluentUI.AspNetCore.Components;

namespace Animarr.Web.Services;

/// <summary>
/// Singleton service that holds the current accent hue and theme mode.
/// Notifies subscribers (MainLayout pushes the hue into a CSS custom property
/// on document.documentElement, Settings page reflects the active swatch).
///
/// Accent is hue-degree based per CANVAS §01.2. The five canonical presets
/// (crimson 25, amber 75, green 150, blue 240, violet 300) drive every
/// `oklch(L C var(--accent-hue))` rule throughout the stylesheet — so a single
/// CSS variable swap rotates the entire palette without re-bundling.
///
/// OfficeColor stays for back-compat with localStorage values written by the
/// pre-FluentDesignTheme code; it's mapped to a hue on read.
/// </summary>
public sealed class ThemeService
{
    public const int DefaultHue = 25; // crimson

    public DesignThemeModes Mode { get; private set; } = DesignThemeModes.System;
    public int AccentHue { get; private set; } = DefaultHue;
    public OfficeColor? AccentColor { get; private set; } = null;

    public event Action? OnChange;

    public void Set(DesignThemeModes mode)
    {
        Mode = mode;
        OnChange?.Invoke();
    }

    /// <summary>Sets accent by hue degree (preferred for the new design tokens).</summary>
    public void SetAccentHue(int hue)
    {
        AccentHue = ((hue % 360) + 360) % 360;
        AccentColor = null;
        OnChange?.Invoke();
    }

    /// <summary>Legacy entry point: maps OfficeColor → hue. Used by stored
    /// localStorage values from the pre-redesign era.</summary>
    public void SetAccentColor(OfficeColor? color)
    {
        AccentColor = color;
        AccentHue   = HueForOfficeColor(color) ?? DefaultHue;
        OnChange?.Invoke();
    }

    /// <summary>Map an OfficeColor to a hue degree. Null → default crimson.</summary>
    public static int? HueForOfficeColor(OfficeColor? color) => color switch
    {
        null                       => null,
        OfficeColor.PowerPoint     => 25,
        OfficeColor.Stream         => 350,
        OfficeColor.Excel          => 150,
        OfficeColor.Sway           => 160,
        OfficeColor.Publisher      => 165,
        OfficeColor.Planner        => 140,
        OfficeColor.Outlook        => 240,
        OfficeColor.Word           => 235,
        OfficeColor.Visio          => 230,
        OfficeColor.Teams          => 280,
        OfficeColor.OneNote        => 290,
        OfficeColor.PowerApps      => 310,
        _                          => 25,
    };
}
