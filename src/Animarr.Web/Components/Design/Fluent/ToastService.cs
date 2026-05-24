// Replacement for Microsoft.FluentUI.AspNetCore.Components.IToastService.
// Same call surface (ShowToast(intent, message), ShowSuccess/Warn/Error/Info)
// so existing callsites work unchanged.

using System.Collections.Concurrent;

namespace Microsoft.FluentUI.AspNetCore.Components;

public interface IToastService
{
    /// <summary>Fires whenever a toast is shown; FluentToastProvider listens.</summary>
    event Action<ToastInfo>? OnShow;

    void ShowToast(ToastIntent intent, string message);
    void ShowSuccess(string message);
    void ShowWarning(string message);
    void ShowError(string message);
    void ShowInfo(string message);
}

public sealed class ToastInfo
{
    public Guid         Id      { get; init; } = Guid.NewGuid();
    public ToastIntent  Intent  { get; init; }
    public string       Message { get; init; } = "";
    public DateTime     ShownAt { get; init; } = DateTime.UtcNow;
}

public sealed class ToastService : IToastService
{
    public event Action<ToastInfo>? OnShow;

    public void ShowToast(ToastIntent intent, string message)
    {
        OnShow?.Invoke(new ToastInfo { Intent = intent, Message = message });
    }

    public void ShowSuccess(string message) => ShowToast(ToastIntent.Success, message);
    public void ShowWarning(string message) => ShowToast(ToastIntent.Warning, message);
    public void ShowError(string message)   => ShowToast(ToastIntent.Error,   message);
    public void ShowInfo(string message)    => ShowToast(ToastIntent.Info,    message);
}
