// Replacement for Microsoft.FluentUI.AspNetCore.Components.IDialogService.
// We only need the ShowDialogAsync<TDialog>(parameters) overload that
// NeedsReviewChip used — and that chip now opens the modal inline, so this
// service is left as a thin no-op to satisfy DI for any leftover callers.

namespace Microsoft.FluentUI.AspNetCore.Components;

public sealed class DialogParameters
{
    public string  Title                       { get; set; } = "";
    public bool    PreventDismissOnOverlayClick { get; set; }
    public bool    PreventScroll               { get; set; }
    public string  Width                       { get; set; } = "";
    public bool    TrapFocus                   { get; set; }
    public bool    Modal                       { get; set; } = true;
}

public interface IDialogReference
{
    Task<DialogResult> Result { get; }
}

public sealed class DialogResult
{
    public bool   Cancelled { get; init; }
    public object? Data     { get; init; }
}

public interface IDialogService
{
    Task<IDialogReference> ShowDialogAsync<TDialog>(DialogParameters parameters) where TDialog : Microsoft.AspNetCore.Components.IComponent;
}

public sealed class DialogService : IDialogService
{
    public Task<IDialogReference> ShowDialogAsync<TDialog>(DialogParameters parameters)
        where TDialog : Microsoft.AspNetCore.Components.IComponent
    {
        // No-op shim: returns an immediately-cancelled reference. Real modal
        // surfaces (NeedsReview, EditMetadata) are now rendered inline in
        // the page tree instead of through this service.
        var tcs = new TaskCompletionSource<DialogResult>();
        tcs.SetResult(new DialogResult { Cancelled = true });
        return Task.FromResult<IDialogReference>(new DialogRef(tcs.Task));
    }

    private sealed class DialogRef(Task<DialogResult> r) : IDialogReference { public Task<DialogResult> Result { get; } = r; }
}
