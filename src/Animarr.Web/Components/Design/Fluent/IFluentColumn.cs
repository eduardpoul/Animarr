namespace Microsoft.FluentUI.AspNetCore.Components;

public interface IFluentColumn<TGridItem>
{
    string?      Title  { get; }
    string?      Width  { get; }
    Align        Align  { get; }
    Microsoft.AspNetCore.Components.RenderFragment Render(TGridItem item);
}

public sealed class PaginationState
{
    public int Page         { get; set; }
    public int PageSize     { get; set; } = 50;
    /// <summary>Alias for PageSize — Fluent's API exposed this name on init.</summary>
    public int ItemsPerPage
    {
        get => PageSize;
        set => PageSize = value;
    }
    public int TotalItems   { get; set; }

    public event Action? Changed;
    public void Go(int page)
    {
        Page = Math.Max(0, page);
        Changed?.Invoke();
    }
}
