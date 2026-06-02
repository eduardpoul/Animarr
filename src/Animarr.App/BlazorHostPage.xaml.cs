namespace Animarr.App;

/// <summary>
/// Pushed over the native catalog to show an existing Blazor screen (settings,
/// pairing, discovery, search, edit-metadata…) at <paramref name="startPath"/>.
/// The native shell owns the hot scroll screens; everything else reuses the
/// mature Blazor UI — no native rewrite, and the WebView config / DI / auth from
/// MauiProgram applies just like the old all-Blazor MainPage.
/// </summary>
public partial class BlazorHostPage : ContentPage
{
    public BlazorHostPage(string startPath)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        WebView.StartPath = string.IsNullOrEmpty(startPath) ? "/" : startPath;
    }
}
