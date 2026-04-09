using Microsoft.FluentUI.AspNetCore.Components;

namespace Animarr.Web.Services;

/// <summary>
/// Singleton service that holds the current design theme mode and notifies
/// subscribers (MainLayout, Settings page) when it changes.
/// </summary>
public sealed class ThemeService
{
    public DesignThemeModes Mode { get; private set; } = DesignThemeModes.System;

    public event Action? OnChange;

    public void Set(DesignThemeModes mode)
    {
        Mode = mode;
        OnChange?.Invoke();
    }
}
