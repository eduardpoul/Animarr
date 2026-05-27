namespace Animarr.UI.Services;

/// <summary>
/// Toast notification intent — drives the inline toast stack's accent
/// colour. Mirrors the Animarr.Web shim that the Razor pages used so
/// migrated pages keep the same call surface.
/// </summary>
public enum ToastIntent
{
    Success,
    Warning,
    Error,
    Info,
}

public sealed record ToastInfo(Guid Id, ToastIntent Intent, string Message, DateTime ShownAt)
{
    public ToastInfo(ToastIntent intent, string message)
        : this(Guid.NewGuid(), intent, message, DateTime.UtcNow) { }
}

/// <summary>
/// Tiny pub-sub for toasts. MainLayout subscribes to <see cref="OnShow"/>
/// and renders the toast stack; pages call ShowSuccess / ShowWarning /
/// ShowError / ShowInfo from anywhere in the tree.
/// </summary>
public sealed class ToastService
{
    public event Action<ToastInfo>? OnShow;

    public void ShowToast(ToastIntent intent, string message)
        => OnShow?.Invoke(new ToastInfo(intent, message));

    public void ShowSuccess(string message) => ShowToast(ToastIntent.Success, message);
    public void ShowWarning(string message) => ShowToast(ToastIntent.Warning, message);
    public void ShowError(string message)   => ShowToast(ToastIntent.Error,   message);
    public void ShowInfo(string message)    => ShowToast(ToastIntent.Info,    message);
}
