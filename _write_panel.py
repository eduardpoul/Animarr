import re

path = r'x:\Repos\Animarr\src\Animarr.Web\Components\Explorer\FolderEditPanel.razor'
content = open(path, encoding='utf-8').read()

# Fix doubled quotes: L[""key""] → L["key"]
fixed = re.sub(r'L\[""\s*([^"]+?)\s*""\]', lambda m: f'L["{m.group(1)}"]', content)
# Fix OnClick ApplyManualIdAsync(""tmdb_tv"") etc.
fixed = re.sub(r'ApplyManualIdAsync\(""\s*([^"]+?)\s*""\)', lambda m: f'ApplyManualIdAsync("{m.group(1)}")', fixed)

changed = sum(1 for a, b in zip(content.split('\n'), fixed.split('\n')) if a != b)
print(f'Lines changed: {changed}')
open(path, 'w', encoding='utf-8').write(fixed)
print('done')

if False:
 content_unused = r"""@inject IDbContextFactory<AppDbContext> DbFactory
@inject IToastService ToastService
@inject FolderWatcherService WatcherService
@inject IPatternMatchService Matcher
@inject MetadataService MetadataService

<!-- Backdrop -->
<div style="position:fixed;inset:0;background:rgba(0,0,0,0.25);z-index:999;" @onclick="@(() => OnClosed.InvokeAsync())"></div>

<!-- Slide-in panel -->
<div style="position:fixed;top:0;right:0;width:680px;height:100vh;background:var(--neutral-layer-floating);border-left:1px solid var(--neutral-stroke-rest);box-shadow:-4px 0 16px rgba(0,0,0,.15);z-index:1000;display:flex;flex-direction:column;overflow:hidden;"
     @onclick:stopPropagation>

    <!-- Header -->
    <div style="display:flex;align-items:center;padding:16px 20px 12px;gap:8px;flex-shrink:0;border-bottom:1px solid var(--neutral-stroke-subtle);">
        <FluentLabel Typo="Typography.H4" Style="flex:1;margin:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">@Folder.Label</FluentLabel>
        <FluentButton Appearance="Appearance.Stealth" IconStart="@(new Icons.Regular.Size20.Dismiss())" Title="@(L[""folders.btn_cancel""])" OnClick="@(() => OnClosed.InvokeAsync())" />
    </div>

    <!-- Tabs -->
    <FluentTabs @bind-ActiveTabId="_activeTab" Style="flex:1;overflow:hidden;display:flex;flex-direction:column;" OnTabChange="OnTabChanged">

        <!-- ═══ TAB 1: General ═══ -->
        <FluentTab Id="general" Label="@(L[""folder_detail.tab_general""])">
            <div style="padding:20px;overflow-y:auto;height:calc(100vh - 120px);box-sizing:border-box;">
                <div style="display:flex;flex-direction:column;gap:14px;max-width:480px;">
                    <FluentTextField @bind-Value="_editLabel"
                                     Label="@(L[""folders.field_name_label""])"
                                     style="width:100%;" />
                    <FluentSwitch @bind-Value="_editWatch">@L["folders.switch_monitor"]</FluentSwitch>
                    <FluentSwitch @bind-Value="_editRenameEnabled">@L["folders.switch_rename"]</FluentSwitch>
                    @if (_editRenameEnabled)
                    {
                        <FluentSelect TOption="string" @bind-Value="_editFolderTypeStr"
                                      Label="@(L[""folders.content_type_label""])" style="width:100%;">
                            <FluentOption TOption="string" Value="Auto">@L["folders.content_type_auto"]</FluentOption>
                            <FluentOption TOption="string" Value="Series">@L["folders.content_type_series"]</FluentOption>
                            <FluentOption TOption="string" Value="Movie">@L["folders.content_type_movie"]</FluentOption>
                        </FluentSelect>
                    }
                    <div>
                        <FluentButton Appearance="Appearance.Accent" OnClick="SaveAsync"
                                      Disabled="@string.IsNullOrWhiteSpace(_editLabel)">
                            @L["folders.btn_save"]
                        </FluentButton>
                    </div>
                </div>
            </div>
        </FluentTab>

        <!-- ═══ TAB 2: Metadata ═══ -->
        <FluentTab Id="metadata" Label="@(L[""folder_detail.tab_metadata""])">
            <div style="padding:20px;overflow-y:auto;height:calc(100vh - 120px);box-sizing:border-box;">
                @if (_metaLoading)
                {
                    <FluentProgress Width="100%" />
                }
                else if (_mediaItem is null)
                {
                    <div style="text-align:center;padding:40px 0;color:var(--neutral-foreground-hint);">
                        <FluentIcon Value="@(new Icons.Regular.Size48.DocumentSearch())" Style="opacity:0.3;" />
                        <div style="margin-top:12px;font-size:14px;">@L["folder_detail.meta_not_identified"]</div>
                        <FluentButton Appearance="Appearance.Accent" Style="margin-top:16px;"
                                      IconStart="@(new Icons.Regular.Size16.DocumentSearch())"
                                      OnClick="ReidentifyAsync">
                            @L["folder_detail.meta_btn_identify"]
                        </FluentButton>
                    </div>
                }
                else
                {
                    <div style="display:flex;flex-direction:column;gap:20px;">

                        <!-- Status badge + re-identify -->
                        <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">
                            <FluentBadge Appearance="@StatusAppearance(_mediaItem.IdentificationStatus)" Style="font-size:12px;">
                                @StatusLabel(_mediaItem.IdentificationStatus)
                            </FluentBadge>
                            @if (_mediaItem.LastMetadataRefreshedAt.HasValue)
                            {
                                <span style="font-size:12px;color:var(--neutral-foreground-hint);">
                                    @L["folder_detail.meta_last_updated"] @_mediaItem.LastMetadataRefreshedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                                </span>
                            }
                            <FluentButton Appearance="Appearance.Stealth"
                                          IconStart="@(new Icons.Regular.Size16.ArrowSync())"
                                          Disabled="@_metaSaving"
                                          OnClick="ReidentifyAsync">
                                @L["folder_detail.meta_btn_reidentify"]
                            </FluentButton>
                        </div>

                        <!-- Poster + Fanart row -->
                        <div style="display:flex;gap:16px;flex-wrap:wrap;">
                            <div style="flex-shrink:0;">
                                <div style="font-size:12px;font-weight:600;color:var(--neutral-foreground-hint);margin-bottom:6px;text-transform:uppercase;letter-spacing:.05em;">@L["folder_detail.meta_poster"]</div>
                                <div style="width:110px;height:165px;background:var(--neutral-layer-2);border-radius:6px;overflow:hidden;border:1px solid var(--neutral-stroke-subtle);">
                                    @if (_mediaItem.PosterPath is not null)
                                    {
                                        <img src="@GetImageUrl(_mediaItem, _mediaItem.PosterPath)" alt="poster" style="width:100%;height:100%;object-fit:cover;" />
                                    }
                                    else
                                    {
                                        <div style="width:100%;height:100%;display:flex;align-items:center;justify-content:center;opacity:0.3;">
                                            <FluentIcon Value="@(new Icons.Regular.Size24.Image())" />
                                        </div>
                                    }
                                </div>
                            </div>
                            <div style="flex:1;min-width:160px;">
                                <div style="font-size:12px;font-weight:600;color:var(--neutral-foreground-hint);margin-bottom:6px;text-transform:uppercase;letter-spacing:.05em;">@L["folder_detail.meta_fanart"]</div>
                                <div style="width:100%;height:165px;background:var(--neutral-layer-2);border-radius:6px;overflow:hidden;border:1px solid var(--neutral-stroke-subtle);">
                                    @if (_mediaItem.FanartPath is not null)
                                    {
                                        <img src="@GetImageUrl(_mediaItem, _mediaItem.FanartPath)" alt="fanart" style="width:100%;height:100%;object-fit:cover;" />
                                    }
                                    else
                                    {
                                        <div style="width:100%;height:100%;display:flex;align-items:center;justify-content:center;opacity:0.3;">
                                            <FluentIcon Value="@(new Icons.Regular.Size24.Image())" />
                                        </div>
                                    }
                                </div>
                            </div>
                        </div>

                        <!-- Basic metadata -->
                        <div style="background:var(--neutral-layer-2);border-radius:6px;padding:14px;font-size:13px;line-height:2;">
                            <div><b>@L["folder_detail.meta_title"]:</b> @(_mediaItem.Title)</div>
                            @if (_mediaItem.OriginalTitle is not null && _mediaItem.OriginalTitle != _mediaItem.Title)
                            {
                                <div><b>@L["folder_detail.meta_original_title"]:</b> @_mediaItem.OriginalTitle</div>
                            }
                            <div><b>@L["folder_detail.meta_year"]:</b> @(_mediaItem.Year?.ToString() ?? "—")</div>
                            <div><b>@L["folder_detail.meta_type"]:</b> @_mediaItem.MediaType</div>
                            @if (_mediaItem.Rating.HasValue)
                            {
                                <div><b>@L["folder_detail.meta_rating"]:</b> &#x2B50; @_mediaItem.Rating.Value.ToString("F1")</div>
                            }
                            @if (_mediaItem.Status is not null)
                            {
                                <div><b>@L["folder_detail.meta_status_lbl"]:</b> @_mediaItem.Status</div>
                            }
                        </div>

                        <FluentDivider />

                        <!-- External IDs -->
                        <FluentLabel Typo="Typography.H3" Style="margin-bottom:4px;">@L["folder_detail.meta_external_ids"]</FluentLabel>
                        <FluentLabel Style="font-size:13px;color:var(--neutral-foreground-hint);margin-bottom:8px;">@L["folder_detail.meta_external_ids_hint"]</FluentLabel>

                        <div style="display:flex;flex-direction:column;gap:14px;max-width:440px;">
                            <!-- TMDB -->
                            <div>
                                <div style="font-size:13px;font-weight:600;margin-bottom:4px;">TMDB ID</div>
                                <div style="display:flex;gap:8px;align-items:flex-end;">
                                    <FluentNumberField TValue="int?" @bind-Value="_editTmdbId"
                                                       Placeholder="e.g. 1399" style="width:150px;" />
                                    <FluentButton Appearance="Appearance.Accent"
                                                  Disabled="@(!_editTmdbId.HasValue || _metaSaving)"
                                                  OnClick="@(() => ApplyManualIdAsync(""tmdb_tv""))">
                                        @L["folder_detail.meta_btn_apply_tmdb_tv"]
                                    </FluentButton>
                                    <FluentButton Appearance="Appearance.Neutral"
                                                  Disabled="@(!_editTmdbId.HasValue || _metaSaving)"
                                                  OnClick="@(() => ApplyManualIdAsync(""tmdb_movie""))">
                                        @L["folder_detail.meta_btn_apply_tmdb_movie"]
                                    </FluentButton>
                                </div>
                                @if (_mediaItem.TmdbId.HasValue)
                                {
                                    <div style="font-size:12px;color:var(--neutral-foreground-hint);margin-top:3px;">
                                        @L["folder_detail.meta_current_id"]: <b>@_mediaItem.TmdbId</b>
                                        &nbsp;&middot;&nbsp;
                                        <a href="https://www.themoviedb.org/tv/@_mediaItem.TmdbId" target="_blank" style="color:var(--accent-fill-rest);">TMDB &#8599;</a>
                                    </div>
                                }
                            </div>
                            <!-- MAL -->
                            <div>
                                <div style="font-size:13px;font-weight:600;margin-bottom:4px;">MyAnimeList ID</div>
                                <div style="display:flex;gap:8px;align-items:flex-end;">
                                    <FluentNumberField TValue="int?" @bind-Value="_editMalId"
                                                       Placeholder="e.g. 5114" style="width:150px;" />
                                    <FluentButton Appearance="Appearance.Accent"
                                                  Disabled="@(!_editMalId.HasValue || _metaSaving)"
                                                  OnClick="@(() => ApplyManualIdAsync(""mal""))">
                                        @L["folder_detail.meta_btn_apply_mal"]
                                    </FluentButton>
                                </div>
                                @if (_mediaItem.MalId.HasValue)
                                {
                                    <div style="font-size:12px;color:var(--neutral-foreground-hint);margin-top:3px;">
                                        @L["folder_detail.meta_current_id"]: <b>@_mediaItem.MalId</b>
                                        &nbsp;&middot;&nbsp;
                                        <a href="https://myanimelist.net/anime/@_mediaItem.MalId" target="_blank" style="color:var(--accent-fill-rest);">MAL &#8599;</a>
                                    </div>
                                }
                            </div>
                        </div>

                        @if (_metaSaving)
                        {
                            <FluentProgress Width="100%" Style="margin-top:8px;" />
                        }
                    </div>
                }
            </div>
        </FluentTab>

        <!-- ═══ TAB 3: Patterns ═══ -->
        <FluentTab Id="patterns" Label="@(L[""folder_detail.tab_patterns""])">
            <div style="padding:20px;overflow-y:auto;height:calc(100vh - 120px);box-sizing:border-box;">
                <div style="display:flex;flex-direction:column;gap:16px;">

                    <FluentLabel Typo="Typography.H3" Style="margin-bottom:2px;">@L["folder_detail.inherited_patterns_title"]</FluentLabel>
                    <FluentLabel Style="font-size:13px;color:var(--neutral-foreground-hint);margin-bottom:4px;">@L["folder_detail.inherited_patterns_hint"]</FluentLabel>

                    @if (_inheritedPatterns.Count == 0)
                    {
                        <FluentLabel Style="color:var(--neutral-foreground-hint);">@L["folder_detail.inherited_patterns_empty"]</FluentLabel>
                    }
                    else
                    {
                        <FluentDataGrid Items="@_inheritedPatterns.AsQueryable()" TGridItem="RenamePattern" ShowHover="true">
                            <TemplateColumn Title="@(L[""patterns.col_priority""])" Width="60px" Align="Align.Center">
                                <span style="opacity:@(_disabledInheritedPatternIds.Contains(context.Id) ? 0.4 : 1.0);">@context.Priority</span>
                            </TemplateColumn>
                            <TemplateColumn Title="@(L[""patterns.col_name""])">
                                <span style="opacity:@(_disabledInheritedPatternIds.Contains(context.Id) ? 0.4 : 1.0);">@context.Name</span>
                            </TemplateColumn>
                            <TemplateColumn Title="" Align="Align.End" Width="120px">
                                @{var isDisabled = _disabledInheritedPatternIds.Contains(context.Id);}
                                <FluentSwitch Value="@(!isDisabled)"
                                              ValueChanged="@(async (bool v) => await ToggleInheritedPatternAsync(context, !v))"
                                              Title="@(isDisabled ? L[""folder_detail.inherited_pattern_disabled""] : L[""folder_detail.inherited_pattern_active""])" />
                            </TemplateColumn>
                        </FluentDataGrid>
                    }

                    <FluentDivider />

                    <div style="display:flex;align-items:center;gap:8px;">
                        <FluentLabel Typo="Typography.H3" Style="flex:1;margin:0;">@L["folder_detail.override_patterns_title"]</FluentLabel>
                        <FluentButton Appearance="Appearance.Accent"
                                      IconStart="@(new Icons.Regular.Size20.Add())"
                                      Title="@(L[""patterns.add_button""])"
                                      OnClick="@(() => OpenEditPatternDialog(null))" />
                    </div>
                    <FluentLabel Style="font-size:13px;color:var(--neutral-foreground-hint);">@L["folder_detail.patterns_hint"]</FluentLabel>

                    @if (_editPatterns.Count == 0)
                    {
                        <FluentLabel Style="color:var(--neutral-foreground-hint);">@L["folder_detail.patterns_empty"]</FluentLabel>
                    }
                    else
                    {
                        <FluentDataGrid Items="@_editPatterns.AsQueryable()" TGridItem="RenamePattern" ShowHover="true">
                            <TemplateColumn Title="@(L[""patterns.col_priority""])" Width="60px" Align="Align.Center">
                                @context.Priority
                            </TemplateColumn>
                            <PropertyColumn Property="@(p => p.Name)" Title="@(L[""patterns.col_name""])" />
                            <TemplateColumn Title="@(L[""patterns.col_regex""])">
                                <code style="font-size:11px;word-break:break-all;">@context.Pattern</code>
                            </TemplateColumn>
                            <TemplateColumn Title="" Align="Align.End" Width="110px">
                                <FluentButton IconStart="@(new Icons.Regular.Size20.Edit())"
                                              Title="@(L[""patterns.btn_edit""])"
                                              OnClick="@(() => OpenEditPatternDialog(context))" />
                                <FluentButton Appearance="Appearance.Stealth"
                                              IconStart="@(new Icons.Regular.Size20.Delete())"
                                              Title="@(L[""patterns.btn_delete""])"
                                              OnClick="@(() => DeleteEditPatternAsync(context))" />
                            </TemplateColumn>
                        </FluentDataGrid>
                    }

                    <FluentDivider Style="margin:4px 0;" />
                    <FluentLabel Typo="Typography.H3" Style="margin-bottom:4px;">@L["patterns.test_title"]</FluentLabel>
                    <FluentLabel Style="font-size:13px;color:var(--neutral-foreground-hint);">@L["folder_detail.test_hint"]</FluentLabel>
                    <div style="display:flex;align-items:flex-end;gap:10px;">
                        <FluentTextField @bind-Value="_editTestInput"
                                         Label="@(L[""patterns.test_field_label""])"
                                         Placeholder="@(L[""patterns.test_placeholder""])"
                                         style="flex:1;" />
                        <FluentButton Appearance="Appearance.Accent" OnClick="TestEditPattern">@L["patterns.test_button"]</FluentButton>
                    </div>
                    @if (_editTestResult is not null)
                    {
                        <div style="padding:12px;background:var(--neutral-layer-2);border-radius:6px;">
                            @if (_editTestResult.IsMatched)
                            {
                                <div style="color:var(--color-fluent-success);font-weight:600;margin-bottom:6px;">@L["patterns.test_matched"]</div>
                                <div style="font-family:monospace;font-size:13px;line-height:1.8;">
                                    @L["patterns.test_season_label"] <b>@(_editTestResult.Season?.ToString() ?? L["patterns.test_season_not_found"])</b><br />
                                    @L["patterns.test_episode_label"] <b>@_editTestResult.Episode</b><br />
                                    @L["patterns.test_kind_label"] <b>@_editTestKind</b><br />
                                    @L["patterns.test_new_name_label"] <b style="color:var(--color-fluent-success);">@(_editTestNewName ?? "—")</b>
                                </div>
                            }
                            else
                            {
                                <div style="color:var(--color-fluent-error);font-weight:600;">@L["patterns.test_not_matched"]</div>
                            }
                        </div>
                    }

                    <FluentDivider />
                    <FluentLabel Typo="Typography.H3" Style="margin-bottom:4px;">@L["folder_detail.rules_title"]</FluentLabel>
                    <FluentLabel Style="font-size:13px;color:var(--neutral-foreground-hint);">@L["folder_detail.rules_hint"]</FluentLabel>
                    <div style="display:flex;align-items:flex-end;gap:8px;">
                        <FluentTextField @bind-Value="_editNewMask"
                                         Placeholder="@(L[""settings.new_mask_placeholder""])"
                                         Label="@(L[""settings.new_mask_label""])"
                                         style="flex:1;" />
                        <FluentButton Appearance="Appearance.Accent"
                                      IconStart="@(new Icons.Regular.Size20.Add())"
                                      OnClick="AddEditRuleAsync"
                                      Title="@(L[""settings.btn_add_mask""])"
                                      Disabled="@string.IsNullOrWhiteSpace(_editNewMask)" />
                    </div>
                    @if (_editRules.Count == 0)
                    {
                        <FluentLabel Style="color:var(--neutral-foreground-hint);">@L["folder_detail.rules_empty"]</FluentLabel>
                    }
                    else
                    {
                        <FluentDataGrid Items="@_editRules.AsQueryable()" TGridItem="IgnoreRule" ShowHover="true">
                            <PropertyColumn Property="@(r => r.Mask)" Title="@(L[""settings.col_mask""])" />
                            <TemplateColumn Title="" Align="Align.End" Width="100px">
                                <FluentButton Appearance="Appearance.Stealth"
                                              IconStart="@(new Icons.Regular.Size20.Delete())"
                                              Title="@(L[""settings.btn_delete_mask""])"
                                              OnClick="@(() => DeleteEditRuleAsync(context))" />
                            </TemplateColumn>
                        </FluentDataGrid>
                    }

                </div>
            </div>
        </FluentTab>
    </FluentTabs>
</div>

<!-- Pattern add/edit dialog -->
<FluentDialog @bind-Hidden="_editPatternDialogHidden" Modal="true" TrapFocus="true" PreventScroll="true" Width="540px">
    <FluentDialogHeader ShowDismiss="true">
        <FluentLabel Typo="Typography.H4">
            @(_editPatternTarget is null ? L["patterns.dialog_add_title"] : L["patterns.dialog_edit_title"])
        </FluentLabel>
    </FluentDialogHeader>
    <FluentDialogBody>
        <FluentStack Orientation="Orientation.Vertical" Style="gap:14px;">
            <FluentTextField @bind-Value="_editPatternName"
                             Label="@(L[""patterns.field_name_label""])"
                             Placeholder="@(L[""patterns.field_name_placeholder""])"
                             style="width:100%;" />
            <FluentTextField @bind-Value="_editPatternRegex"
                             Label="@(L[""patterns.field_regex_label""])"
                             Placeholder="@(L[""patterns.field_regex_placeholder""])"
                             style="width:100%;font-family:monospace;" />
            <FluentNumberField TValue="int" @bind-Value="_editPatternPriority"
                               Label="@(L[""patterns.field_priority_label""])"
                               Min="1" Max="999" style="width:160px;" />
        </FluentStack>
    </FluentDialogBody>
    <FluentDialogFooter>
        <FluentButton Appearance="Appearance.Accent" OnClick="SaveEditPatternAsync"
                      Disabled="@(string.IsNullOrWhiteSpace(_editPatternName) || string.IsNullOrWhiteSpace(_editPatternRegex))">
            @L["patterns.btn_save"]
        </FluentButton>
        <FluentButton OnClick="@(() => _editPatternDialogHidden = true)">@L["patterns.btn_cancel"]</FluentButton>
    </FluentDialogFooter>
</FluentDialog>

@code {
    [Parameter, EditorRequired] public FolderWatcher Folder { get; set; } = null!;
    [Parameter] public EventCallback OnSaved { get; set; }
    [Parameter] public EventCallback OnClosed { get; set; }

    private Guid _loadedFolderId;
    private string _activeTab = "general";

    // ── Tab 1: General ────────────────────────────────────────────
    private string _editLabel = "";
    private bool _editWatch = true;
    private bool _editRenameEnabled = true;
    private string _editFolderTypeStr = "Auto";

    // ── Tab 2: Metadata ───────────────────────────────────────────
    private MediaItem? _mediaItem;
    private bool _metaLoading = false;
    private bool _metaSaving  = false;
    private int? _editTmdbId;
    private int? _editMalId;

    // ── Tab 3: Patterns ───────────────────────────────────────────
    private List<RenamePattern> _editPatterns = [];
    private List<RenamePattern> _inheritedPatterns = [];
    private HashSet<Guid> _disabledInheritedPatternIds = [];
    private bool _editPatternDialogHidden = true;
    private RenamePattern? _editPatternTarget;
    private string _editPatternName = "";
    private string _editPatternRegex = "";
    private int _editPatternPriority = 100;
    private string _editTestInput = "";
    private ParseResult? _editTestResult;
    private string? _editTestNewName;
    private string _editTestKind = "";

    private List<IgnoreRule> _editRules = [];
    private string _editNewMask = "";

    protected override async Task OnParametersSetAsync()
    {
        if (Folder.Id == _loadedFolderId) return;
        _loadedFolderId = Folder.Id;
        _activeTab      = "general";
        _mediaItem      = null;
        _editTmdbId     = null;
        _editMalId      = null;

        _editLabel         = Folder.Label;
        _editWatch         = Folder.WatchEnabled;
        _editRenameEnabled = Folder.RenameEnabled;
        _editFolderTypeStr = Folder.FolderType.ToString();
        _editTestInput     = "";
        _editTestResult    = null;
        _editNewMask       = "";
        _editPatternDialogHidden = true;

        await using var db = await DbFactory.CreateDbContextAsync();
        _editPatterns = await db.RenamePatterns
            .Where(p => p.FolderId == Folder.Id && p.Scope == PatternScope.FolderOverride && !p.IsExcluded)
            .OrderBy(p => p.Priority)
            .ToListAsync();
        _inheritedPatterns = await db.RenamePatterns
            .Where(p => p.Scope == PatternScope.Global)
            .OrderBy(p => p.Priority)
            .ToListAsync();
        _disabledInheritedPatternIds = (await db.RenamePatterns
            .Where(p => p.FolderId == Folder.Id && p.IsExcluded && p.GlobalPatternId.HasValue)
            .Select(p => p.GlobalPatternId!.Value)
            .ToListAsync()).ToHashSet();
        _editRules = await db.IgnoreRules
            .Where(r => r.FolderId == Folder.Id && r.Scope == RuleScope.FolderOverride)
            .OrderBy(r => r.Mask)
            .ToListAsync();
    }

    private async Task OnTabChanged(FluentTab tab)
    {
        if (tab.Id == "metadata" && _mediaItem is null && !_metaLoading)
            await LoadMediaItemAsync();
    }

    private async Task LoadMediaItemAsync()
    {
        _metaLoading = true;
        StateHasChanged();
        await using var db = await DbFactory.CreateDbContextAsync();
        _mediaItem = await db.MediaItems
            .Include(m => m.Folder)
            .FirstOrDefaultAsync(m => m.FolderId == Folder.Id);
        if (_mediaItem is not null)
        {
            _editTmdbId = _mediaItem.TmdbId;
            _editMalId  = _mediaItem.MalId;
        }
        _metaLoading = false;
    }

    // ── Tab 1: General ────────────────────────────────────────────

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_editLabel)) return;
        var folderType = Enum.TryParse<FolderType>(_editFolderTypeStr, out var ft) ? ft : FolderType.Auto;

        await using var db = await DbFactory.CreateDbContextAsync();
        var entity = await db.FolderWatchers.FindAsync(Folder.Id);
        if (entity is null) return;

        var wasEnabled = entity.WatchEnabled;
        entity.Label         = _editLabel.Trim();
        entity.WatchEnabled  = _editWatch;
        entity.RenameEnabled = _editRenameEnabled;
        entity.FolderType    = _editRenameEnabled ? folderType : FolderType.Auto;
        await db.SaveChangesAsync();

        if (!wasEnabled && _editWatch) await WatcherService.StartWatcherAsync(entity.Id);
        else if (wasEnabled && !_editWatch) await WatcherService.StopWatcherAsync(entity.Id);

        ToastService.ShowToast(ToastIntent.Success, L.Get("explorer.toast_folder_updated", entity.Label));
        await OnSaved.InvokeAsync();
    }

    // ── Tab 2: Metadata ───────────────────────────────────────────

    private string GetImageUrl(MediaItem item, string relativePath)
    {
        if (item.Folder is null) return "";
        var full = Path.Combine(item.Folder.Path, relativePath);
        return $"/api/image?path={Uri.EscapeDataString(full)}";
    }

    private static Appearance StatusAppearance(IdentificationStatus s) => s switch
    {
        IdentificationStatus.Identified  => Appearance.Accent,
        IdentificationStatus.Manual      => Appearance.Accent,
        IdentificationStatus.NeedsReview => Appearance.Neutral,
        IdentificationStatus.Failed      => Appearance.Neutral,
        _                                => Appearance.Neutral,
    };

    private string StatusLabel(IdentificationStatus s) => s switch
    {
        IdentificationStatus.Identified  => L["folder_detail.meta_status_identified"],
        IdentificationStatus.Manual      => L["folder_detail.meta_status_manual"],
        IdentificationStatus.NeedsReview => L["folder_detail.meta_status_needs_review"],
        IdentificationStatus.Failed      => L["folder_detail.meta_status_failed"],
        _                                => L["folder_detail.meta_status_pending"],
    };

    private async Task ReidentifyAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        await db.IdentificationQueues
            .Where(q => q.FolderId == Folder.Id &&
                        (q.Status == IdentificationQueueStatus.Queued ||
                         q.Status == IdentificationQueueStatus.Failed ||
                         q.Status == IdentificationQueueStatus.Done))
            .ExecuteDeleteAsync();
        db.IdentificationQueues.Add(new IdentificationQueue
        {
            Id           = Guid.NewGuid(),
            FolderId     = Folder.Id,
            ForceRefresh = true,
            QueuedAt     = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        ToastService.ShowToast(ToastIntent.Success, L["folders.toast_queued"]);
    }

    private async Task ApplyManualIdAsync(string source)
    {
        int? id = source == "mal" ? _editMalId : _editTmdbId;
        if (id is null) return;
        _metaSaving = true;
        StateHasChanged();
        try
        {
            await MetadataService.ApplyManualAsync(Folder.Id, source, id.Value);
            ToastService.ShowToast(ToastIntent.Success, L["folder_detail.meta_toast_applied"]);
            _mediaItem = null;
            await LoadMediaItemAsync();
        }
        catch (Exception ex)
        {
            ToastService.ShowToast(ToastIntent.Error, ex.Message);
        }
        finally
        {
            _metaSaving = false;
        }
    }

    // ── Tab 3: Patterns ───────────────────────────────────────────

    private void OpenEditPatternDialog(RenamePattern? p)
    {
        _editPatternTarget   = p;
        _editPatternName     = p?.Name ?? "";
        _editPatternRegex    = p?.Pattern ?? "";
        _editPatternPriority = p?.Priority ?? 100;
        _editPatternDialogHidden = false;
    }

    private async Task SaveEditPatternAsync()
    {
        if (string.IsNullOrWhiteSpace(_editPatternName) || string.IsNullOrWhiteSpace(_editPatternRegex)) return;
        await using var db = await DbFactory.CreateDbContextAsync();
        if (_editPatternTarget is null)
        {
            db.RenamePatterns.Add(new RenamePattern
            {
                Id        = Guid.NewGuid(),
                Name      = _editPatternName.Trim(),
                Pattern   = _editPatternRegex.Trim(),
                Priority  = _editPatternPriority,
                Scope     = PatternScope.FolderOverride,
                FolderId  = Folder.Id,
                IsBuiltIn = false,
            });
            ToastService.ShowToast(ToastIntent.Success, L["patterns.toast_added"]);
        }
        else
        {
            var entity = await db.RenamePatterns.FindAsync(_editPatternTarget.Id);
            if (entity is null) return;
            entity.Name     = _editPatternName.Trim();
            entity.Pattern  = _editPatternRegex.Trim();
            entity.Priority = _editPatternPriority;
            ToastService.ShowToast(ToastIntent.Success, L["patterns.toast_updated"]);
        }
        await db.SaveChangesAsync();
        _editPatternDialogHidden = true;
        await ReloadEditPatternsAsync();
    }

    private async Task DeleteEditPatternAsync(RenamePattern p)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var entity = await db.RenamePatterns.FindAsync(p.Id);
        if (entity is not null) { db.RenamePatterns.Remove(entity); await db.SaveChangesAsync(); }
        ToastService.ShowToast(ToastIntent.Success, L.Get("patterns.toast_deleted", p.Name));
        await ReloadEditPatternsAsync();
    }

    private async Task ReloadEditPatternsAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _editPatterns = await db.RenamePatterns
            .Where(p => p.FolderId == Folder.Id && p.Scope == PatternScope.FolderOverride && !p.IsExcluded)
            .OrderBy(p => p.Priority)
            .ToListAsync();
    }

    private async Task ToggleInheritedPatternAsync(RenamePattern globalPattern, bool disable)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var existing = await db.RenamePatterns
            .FirstOrDefaultAsync(p => p.FolderId == Folder.Id && p.IsExcluded && p.GlobalPatternId == globalPattern.Id);
        if (disable && existing is null)
        {
            db.RenamePatterns.Add(new RenamePattern
            {
                Id              = Guid.NewGuid(),
                Name            = $"Exclude: {globalPattern.Name}",
                Pattern         = globalPattern.Pattern,
                Scope           = PatternScope.FolderOverride,
                FolderId        = Folder.Id,
                IsExcluded      = true,
                GlobalPatternId = globalPattern.Id,
                Priority        = globalPattern.Priority,
            });
            _disabledInheritedPatternIds.Add(globalPattern.Id);
        }
        else if (!disable && existing is not null)
        {
            db.RenamePatterns.Remove(existing);
            _disabledInheritedPatternIds.Remove(globalPattern.Id);
        }
        await db.SaveChangesAsync();
    }

    private void TestEditPattern()
    {
        if (string.IsNullOrWhiteSpace(_editTestInput)) return;
        var ext  = Path.GetExtension(_editTestInput);
        var kind = Matcher.DetermineFileKind(ext);
        _editTestKind    = kind.ToString();
        _editTestResult  = Matcher.ParseFileName(_editTestInput, _editPatterns);
        _editTestNewName = _editTestResult.IsMatched
            ? Matcher.BuildTargetName(_editTestResult, null, kind, ext)
            : null;
    }

    // ── Ignore rules ──────────────────────────────────────────────

    private async Task AddEditRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(_editNewMask)) return;
        var mask = _editNewMask.Trim().ToLowerInvariant();
        if (_editRules.Any(r => r.Mask.Equals(mask, StringComparison.OrdinalIgnoreCase)))
        {
            ToastService.ShowToast(ToastIntent.Warning, L["settings.toast_mask_exists"]);
            return;
        }
        await using var db = await DbFactory.CreateDbContextAsync();
        db.IgnoreRules.Add(new IgnoreRule
        {
            Id       = Guid.NewGuid(),
            Mask     = mask,
            Scope    = RuleScope.FolderOverride,
            FolderId = Folder.Id,
        });
        await db.SaveChangesAsync();
        _editNewMask = "";
        ToastService.ShowToast(ToastIntent.Success, L.Get("settings.toast_rule_added", mask));
        await ReloadEditRulesAsync();
    }

    private async Task DeleteEditRuleAsync(IgnoreRule rule)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var entity = await db.IgnoreRules.FindAsync(rule.Id);
        if (entity is not null) { db.IgnoreRules.Remove(entity); await db.SaveChangesAsync(); }
        ToastService.ShowToast(ToastIntent.Success, L.Get("settings.toast_rule_deleted", rule.Mask));
        await ReloadEditRulesAsync();
    }

    private async Task ReloadEditRulesAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        _editRules = await db.IgnoreRules
            .Where(r => r.FolderId == Folder.Id && r.Scope == RuleScope.FolderOverride)
            .OrderBy(r => r.Mask)
            .ToListAsync();
    }
}
"""

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print('done')
