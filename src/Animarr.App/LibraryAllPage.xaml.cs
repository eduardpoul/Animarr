using System.Net.Http.Json;
using System.Text.Json;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// Full library ("Просмотреть все" from the Home library block): every title as
/// a focusable poster card in an alphabetical wrap-grid. Cards are code-built
/// (TvCards) with programmatic D-pad commands. BACK pops to Home.
/// </summary>
public partial class LibraryAllPage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ServerAddressProvider _addr;
    private readonly Animarr.Shared.IAnimarrApiClient _api;
    private string ImageBase => _addr.Current!.ToString().TrimEnd('/');

    private ApiItem[] _items = Array.Empty<ApiItem>();
    private List<(Guid Id, string Label)> _sections = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _sectionFolderIds = new();
    private Guid? _activeSection;

    public LibraryAllPage(Guid? activeSection = null)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _http = services.GetRequiredService<HttpClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();
        _api  = services.GetRequiredService<Animarr.Shared.IAnimarrApiClient>();
        _activeSection = activeSection;

        PageTitle.Text = TvL.T("home.library_all", "Вся библиотека", "Full library");

        // D-pad OK via the behavior (programmatic — the reliable path).
        BackFocus.Command = new Command(async () => await Navigation.PopAsync());

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var itemsTask    = _http.GetFromJsonAsync<ApiItem[]>("/api/media?take=1000", Json);
            var sectionsTask = SafeAsync(_api.GetSectionFoldersAsync());
            var foldersTask  = SafeAsync(_api.GetFoldersAsync());

            _items = await itemsTask ?? Array.Empty<ApiItem>();

            var sections = await sectionsTask;
            var folders  = await foldersTask;
            _sections = sections?.OrderBy(s => s.Label)
                                 .Select(s => (s.Id, (string)s.Label))
                                 .ToList() ?? new();
            _sectionFolderIds.Clear();
            if (sections is not null)
            {
                foreach (var sec in sections)
                {
                    var ids = folders?.Where(f => f.ParentSectionId == sec.Id)
                                      .Select(f => f.Id).ToHashSet() ?? new HashSet<Guid>();
                    ids.Add(sec.Id);
                    _sectionFolderIds[sec.Id] = ids;
                }
            }
            if (_activeSection is Guid pre && _sections.All(s => s.Id != pre))
                _activeSection = null;

            BuildSections();
            BuildGrid();
        }
        catch
        {
            CountLabel.Text = TvL.T("home.library_title", "Библиотека", "Library").ToUpperInvariant()
                              + " · " + TvL.T("catalog.error", "ошибка загрузки", "load error");
        }
        finally { Loader.IsVisible = false; }
    }

    private static async Task<T?> SafeAsync<T>(Task<T> task)
    {
        try { return await task; } catch { return default; }
    }

    private void BuildSections()
    {
        SectionsHost.Children.Clear();
        if (_sections.Count == 0) return;
        SectionsHost.Children.Add(TvCards.BuildFilterChip(
            TvL.T("catalog.tab_all", "Все", "All"), _activeSection is null,
            new Command(() => SwitchSection(null))));
        foreach (var (id, label) in _sections)
        {
            var sid = id;
            SectionsHost.Children.Add(TvCards.BuildFilterChip(
                label, _activeSection == sid, new Command(() => SwitchSection(sid))));
        }
    }

    private void SwitchSection(Guid? id)
    {
        if (_activeSection == id) return;
        _activeSection = id;
        BuildSections();
        BuildGrid();
    }

    private void BuildGrid()
    {
        IEnumerable<ApiItem> src = _items;
        if (_activeSection is Guid sec && _sectionFolderIds.TryGetValue(sec, out var ids))
            src = src.Where(i => Guid.TryParse(i.FolderId, out var f) && ids.Contains(f));

        var all = src.Where(i => !string.IsNullOrEmpty(i.PosterPath ?? i.FanartPath))
                     .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                     .ToList();

        CountLabel.Text =
            $"{TvL.T("home.library_title", "Библиотека", "Library").ToUpperInvariant()} · {all.Count}";
        GridHost.Children.Clear();
        foreach (var i in all)
            GridHost.Children.Add(TvCards.BuildPosterCard(ToPoster(i)));
    }

    private CatalogNativePage.PosterItem ToPoster(ApiItem i)
    {
        var path  = i.PosterPath ?? i.FanartPath!;
        var parts = new List<string>();
        if (i.Year is > 0)         parts.Add(i.Year!.Value.ToString());
        if (i.EpisodeCount is > 0) parts.Add($"{i.EpisodeCount} EP");
        if (i.Rating is > 0)       parts.Add($"★ {i.Rating:F1}");

        var p = new CatalogNativePage.PosterItem
        {
            Id          = i.Id ?? "",
            Title       = (i.Title ?? "").ToUpperInvariant(),
            ImageUrl    = $"{ImageBase}/api/image?path={Uri.EscapeDataString(path)}&w=330",
            BackdropUrl = string.IsNullOrEmpty(i.FanartPath) ? null
                        : $"{ImageBase}/api/image?path={Uri.EscapeDataString(i.FanartPath)}&w=1280",
            TypeLabel   = TypeLabel(i.MediaType),
            Meta        = string.Join(" · ", parts),
        };
        p.Open = new Command(async () =>
            await Navigation.PushAsync(new NativeDetailPage(p.Id, p.Title, p.BackdropUrl)));
        return p;
    }

    private static string TypeLabel(string? t) => t switch
    {
        "Anime"       => "ANIME",
        "Movie"       => "MOVIE",
        "Series"      => "SERIES",
        "Multserials" => "MULTI",
        _             => "",
    };

    private sealed record ApiItem(
        string? Id, string? Title, string? PosterPath, string? FanartPath,
        int? Year, string? MediaType, double? Rating, int? EpisodeCount, string? FolderId);
}
