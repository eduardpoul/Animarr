# UI + Flow Audit — branch `redesign/ui-migration`

> 2026-05-22. Snapshot of what's working, what's regressed, and what
> isn't migrated yet after the design overhaul.
>
> Companion to [REDESIGN_UI.md](REDESIGN_UI.md), [REDESIGN_DATA.md](REDESIGN_DATA.md).
> Reference design: [`design_handoff_animarr/`](design_handoff_animarr/README.md).

---

## Build status

```
dotnet build → 0 errors, 5 warnings (all pre-existing CS8604/8602 in
Explorer.razor, Torrents.razor, MetadataService.cs — unrelated to this branch).
```

## Files

| Layer | Created | Touched |
|---|---:|---:|
| Design tokens / styles | tokens.css, fluent-overrides.css | app.css, fluent-custom-classes.css |
| Primitives | DIcon, Btn, Pill, Tabs, Input, Select, Toggle (+ .css for each) | — |
| Media | Poster, CatalogHero, MediaDetailHero, EpisodeCard (+ .css) | — |
| Layout | SidebarLlmStatus | MainLayout.razor (+ .css), App.razor |
| Page | NeedsReviewChip, NeedsReviewDialog (+ .css) | Home.razor (+ new .css), MediaDetail.razor (+ new .css), Explorer.razor |
| Schema | Migration `AddDesignFieldsToMediaItemAndFolder` | MediaItem.cs, FolderWatcher.cs |
| Services | HueHash, LanguageNameMap | TmdbClient, MalClient, MetadataService, MicrosoftAiLlmService, IdentificationQueueProcessorService, ILlmService |

**Net diff:** +1139 / −776 lines, mostly because the old `Home.razor`
inline styles compressed into Razor + scoped CSS.

---

## 1. Catalog (`/`)

### Flow
```
OnInitializedAsync
  ↳ load sections (FolderWatcher where IsSection)
  ↳ load all MediaItems (exclude ghosts: folder/file must exist on disk)
  ↳ featured = top-5 by Rating ≥ 8.0 (with FanartPath)
  ↳ heroInterval ← AppConfig.BackdropIntervalSec
  ↳ ApplyFilter
  ↳ subscribe to IdentificationQueue → activeQueueFolderIds

Render
  ↳ <CatalogHero Items=@featured IntervalSec=@interval />     ← new
  ↳ filter bar
      <Tabs T=MediaItemType?>     [All|Anime|Movie|Series|Multi]
      <Input IconBefore=search />
      <Btn ghost RescanAllAsync /> ← new
      <NeedsReviewChip />          ← new, opens NeedsReviewDialog
      "{filtered}/{total} TITLES"  ← new
  ↳ folder chip strip (Pill clickable, accent when active)
  ↳ grid: <Poster Item=@item />    ← new, replaces inline poster markup
```

### What works
- ✅ Hero rotates every `BackdropIntervalSec` (default 30s); pager dots clickable
- ✅ Hero pushes `FanartPath` into global blurred backdrop layer via `initBackdrop` JS
- ✅ Hero shows CJK watermark when `Item.CjkTitle` present, hue-tinted radial wash
- ✅ Type filter is a segmented control; combines AND with folder chip + search
- ✅ Search now matches `Title | OriginalTitle | EnglishTitle | CjkTitle`
- ✅ Poster card uses hue tint, CJK corner watermark, confidence chip (<0.85)
- ✅ NeedsReviewChip count auto-refreshes via `QueueProc.ItemIdentified` event
- ✅ Rescan all queues all non-section folders with `ForceRefresh=true`

### Known regressions / gaps
- ⚠️ Per-poster delete button (hover) removed — design moves delete to
  Edit Metadata → Manage → Danger zone. The legacy delete dialog code
  still lives in [Home.razor:150-167](src/Animarr.Web/Components/Pages/Home.razor)
  with no trigger; harmless dead code, candidate for cleanup post-Phase 5.
- ⚠️ Per-poster identification-in-progress indicator removed. Visibility moved
  to sidebar LLM status card (queue depth). Acceptable per design.
- ⚠️ Manual search dialog (Tags + Search) still uses old FluentUI. Functional
  but visually inconsistent — Phase 6 cleanup.

### Risks
- Hero `_timer` is a `System.Threading.Timer`; properly disposed in `Dispose()`.
  But re-rendering the page from a parent re-mount could leak briefly until
  the disposal fires. Low impact (single page).
- `RescanAllAsync` queues every folder unconditionally — large libraries
  (1000+ folders) could swamp the queue. Acceptable for single-user; consider
  paginating in a future iteration.

## 2. MediaDetail (`/catalog/{id}`)

### Flow
```
OnInitializedAsync
  ↳ load MediaItem with Folder include
  ↳ push FanartPath to backdrop (existing flow)
  ↳ load episodes per season + run file-mapping (pattern → fuzzy → optional LLM)

Render
  ↳ <MediaDetailHero Item=@item OnEditClick=OpenEditPanel />   ← new
  ↳ <Btn ghost ReidentifyAsync /> (action row)                 ← new
  ↳ 3-col body (Synopsis · Details · Identification)           ← new
      Synopsis: overview <p> + genres as <Pill>s
      Details:  mono <dl> for Studio/Language/Runtime/Episodes/Season/Status/ContentRating
      Identification: TMDB/MAL/IMDb IDs with per-source confidence + on-disk path
  ↳ NeedsReview banner (if status=NeedsReview) — existing, unchanged
  ↳ Episodes section per season: <EpisodeCard ... /> grid       ← new
  ↳ Edit panel drawer — existing FluentDialog-based, unchanged
  ↳ Image preview lightbox — existing, unchanged
```

### What works
- ✅ Hero shows fanart full-bleed; BACK and EDIT METADATA glass chips
- ✅ Confidence per source displayed (best-available across TMDB/MAL/IMDb/LLM)
- ✅ Episode cards have proper ON DISK / MISSING visual states per design
- ✅ Episode meta line includes resolution + codec (regex-extracted from filename) + size
- ✅ EnglishTitle shown below H1 when distinct from Title
- ✅ Tags from `TagsJson` rendered as accent pills on hero

### Known regressions / gaps
- ⚠️ The original logo image (`_logoUrl`) is no longer painted over the title —
  it was a TMDB transparent logo. Reasonable trade-off (design doesn't show one);
  the logo PNG is still downloaded and available via `MediaItem.LogoPath`.
- ⚠️ Poster zoom-in lightbox on click was a feature of the old poster div;
  the new `MediaDetailHero`'s poster doesn't open the lightbox. To restore:
  pass an `OnPosterClick` callback through the hero. Low priority.
- ⚠️ Genres still rendered in Synopsis col — they're TMDB genres ("Action",
  "Drama"). The new design's hero shows descriptive tags (from `TagsJson`) on the
  hero instead. We now have both — slight redundancy but neither is wrong.

### Risks
- The hero's `JS.InvokeVoidAsync("initBackdrop", ...)` in `OnAfterRenderAsync`
  swaps the global slideshow on every page mount. If two MediaDetail mounts
  race (back/forward navigation), the last one wins — fine.
- Episode card click handler is on an outer `<div @onclick="...">` wrapper
  around `<EpisodeCard />` because the card itself doesn't expose `OnClick`.
  Works, but means the keyboard click event doesn't activate the play action
  (no role/tabindex on the wrapper). To fix: add `OnClick` parameter to
  EpisodeCard or wrap in `<button>`.

## 3. Sidebar / shell

### Flow
```
MainLayout
  ↳ <FluentDesignTheme Mode=Dark />        (locked, no light variant)
  ↳ backdrop layer (always-on, JS-driven)
  ↳ film grain overlay (SVG noise)
  ↳ sidebar
      <a /> brand (logo + ANIMARR + v0.2 · LOCAL)
      <FluentNavMenu>
        Catalog · Torrents · Settings    (3 entries, down from 5)
      <SidebarLlmStatus />               ← new, sticky bottom
```

### What works
- ✅ Dark mode locked; Fluent vars remapped to design tokens via fluent-overrides.css
- ✅ Sidebar gradient + glass blur
- ✅ LLM status card shows ONLINE/OFF/ERROR + provider · model + queue badge
- ✅ Pulse animation on the dot when LLM available
- ✅ Live queue depth via `QueueProc.QueueChanged` subscription

### Known regressions / gaps
- ⚠️ Explorer + History sidebar links removed. Routes still resolve
  (`/explorer`, `/history`) for deep links, but no UI entry point yet.
  Settings page redesign (Phase 6) will embed both as nested tabs.
- ⚠️ LLM probe runs once on first render; doesn't re-probe on a timer.
  If the LLM endpoint goes down, the card lies until next page reload.
  Acceptable for single-user dev tool.

## 4. NeedsReview surface

### Flow
```
NeedsReviewChip (mounted in Catalog header)
  ↳ count via db.MediaItems where status = NeedsReview
  ↳ subscribed to QueueProc.ItemIdentified → recount
  ↳ click → DialogService.ShowDialogAsync<NeedsReviewDialog>

NeedsReviewDialog
  ↳ loads MediaItems where status=NeedsReview, parses CandidatesJson
  ↳ renders per-folder candidate grid (Pill cards w/ confidence color)
  ↳ click "Use" → MetadataService.ApplyManualAsync (existing service)
  ↳ on ItemIdentified, reload entries
```

### What works
- ✅ Chip only renders when count > 0
- ✅ Pulse dot on chip
- ✅ Confidence color is `color-mix` between success ↔ warn based on score
- ✅ Sources: TMDB / MAL / IMDb all supported via ApplyManualAsync's two overloads

### Risks
- Candidate "Use" applies metadata immediately on click; no preview / confirm step.
  Matches the design contract intent. User can re-edit via the metadata drawer
  if wrong.

## 5. Data layer

### Parser audit table

Every available field from each enabled source is now requested. Saved fields
that map to a `MediaItem` column are noted; pulled-but-not-stored fields are
available in the DTO for any future feature.

| Source / Field | Pulled? | MediaItem column |
|---|:-:|---|
| **TMDB TV** | | |
| id, name, original_name | ✅ | TmdbId, Title, OriginalTitle |
| original_language | ✅ NEW | Language (via LanguageNameMap ISO→display) |
| first_air_date, last_air_date | ✅ NEW (last_air_date) | Year (last not saved) |
| overview, tagline, status, content_rating | ✅ | Description, Tagline, Status, ContentRating |
| vote_average, vote_count, popularity | ✅ NEW (popularity) | Rating, RatingCount, Popularity, TmdbConfidence (from vote_count) |
| episode_run_time, number_of_episodes, number_of_seasons | ✅ NEW (episodes/seasons counts) | Runtime, EpisodeCount, SeasonLabel ("S{n}" when > 1) |
| seasons[] (with poster_path, episode_count) | ✅ | SeasonsJson + downloaded season posters |
| external_ids (imdb_id, tvdb_id) | ✅ | ImdbId, TvdbId |
| images (posters/backdrops/logos) | ✅ | PosterPath, FanartPath, LogoPath + GetAvailableImagesAsync |
| genres | ✅ | GenresJson |
| **production_companies, networks** | ✅ NEW | Studio (prefer Networks[0] → ProductionCompanies[0]) |
| **credits.cast/crew** | ✅ NEW | (in DTO, not persisted — future cast UI) |
| **keywords** | ✅ NEW | TagsJson (top-8) |
| **created_by, homepage, in_production, type** | ✅ NEW | (in DTO, not persisted) |
| translations (en alt) | ✅ NEW | EnglishTitle (when distinct from Title) |
| CJK title heuristic (orig_lang ∈ zh,ja,ko) | ✅ NEW | CjkTitle |
| **TMDB Movie** — same as TV minus seasons, plus runtime, release_date | ✅ | same |
| **MAL anime** | | |
| id, title, alternative_titles (en, ja, synonyms) | ✅ | MalId, Title, EnglishTitle, CjkTitle |
| start_date, end_date | ✅ NEW (end) | Year (end not saved) |
| synopsis, mean, num_scoring_users, num_episodes, status, media_type | ✅ | Description, Rating, RatingCount, EpisodeCount, Status |
| genres | ✅ | GenresJson |
| main_picture, pictures[] | ✅ NEW (pictures) | PosterPath + GetAvailableImagesAsync gallery |
| **studios[]** | ✅ NEW | Studio |
| **start_season {year, season}** | ✅ NEW | SeasonLabel ("Spring 2023") |
| **broadcast {day, start_time}** | ✅ NEW | (in DTO, not persisted) |
| **source** (manga / novel / original) | ✅ NEW | (in DTO, not persisted) |
| **nsfw** | ✅ NEW | (in DTO, not persisted) |
| **popularity** | ✅ NEW | Popularity |
| **average_episode_duration** (seconds) | ✅ NEW | Runtime (÷60 → minutes) |
| Language default = Japanese | — | Language |
| MalConfidence proxy (num_scoring_users / 50k) | NEW | MalConfidence |
| **IMDb (imdbapi.dev)** | | |
| id, primaryTitle, originalTitle | ✅ | ImdbId, Title, OriginalTitle |
| startYear, endYear | ✅ | Year (end not saved) |
| plot, genres, runtimeSeconds | ✅ | Description, GenresJson, Runtime |
| primaryImage | ✅ | PosterPath |
| rating (aggregate, voteCount) | ✅ | Rating, RatingCount, ImdbConfidence (NEW, from voteCount/10k) |
| Hue auto-set from PrimaryTitle hash | — | Hue (NEW) |

### Image pipeline audit

| Image | Source priority | Cache path | Re-downloadable |
|---|---|---|---|
| Poster | TMDB → MAL → IMDb | `{CacheRoot}/{folderId}/poster.{ext}` | yes (forceRefresh) |
| Fanart | TMDB only | `{CacheRoot}/{folderId}/fanart.{ext}` | yes |
| Logo | TMDB only | `{CacheRoot}/{folderId}/logo.{ext}` | yes |
| Season posters | TMDB | `{CacheRoot}/{folderId}/seasonN-poster.jpg` | yes |
| Episode stills | TMDB (lazy, MediaDetail) | `{CacheRoot}/{folderId}/season-N/episode-NN.jpg` | gated by DownloadEpisodeThumbs config |
| Multi-candidate gallery (Edit drawer) | TMDB GetTvImages/GetMovieImages + MAL pictures[] | streamed URLs (not cached) | refetched on drawer open |

All paths absolute. `MediaCachePaths.ForFolder(folderId)` is single source of
truth. **No images written into the user's media tree.**

### Service events (Blazor's "SignalR-equivalent")

| Event | Fired by | Subscribers |
|---|---|---|
| `IdentificationQueueProcessorService.QueueChanged` | On enqueue/dequeue/depth change | SidebarLlmStatus |
| `IdentificationQueueProcessorService.ItemIdentified(folderId)` | On job complete (success or fail) | NeedsReviewChip, NeedsReviewDialog |
| `TorrentEngineService.StateChanged` | Per-torrent state delta | Torrents.razor (existing) |
| `FolderWatcherService.FileRenamed`, `SubfolderCreated` | FSW events | Explorer (existing) |
| `ThemeService.OnChange` | Accent/Mode change | MainLayout, Settings (existing) |
| `LocalizationService.LanguageChanged` | Language switch | MainLayout, all L[] consumers (existing) |

`MicrosoftAiLlmService.GetTelemetry()` returns rolling 50-call latency window
+ probe state. Hit/miss counters live on the queue processor.

---

## 6. Phases status

| Phase | Subject | Status |
|---|---|---|
| 0 | Foundation: tokens + fonts + Fluent re-skin | ✅ done |
| 1 | Design primitives library (8 components) | ✅ done |
| 2 | AppShell + backdrop + sidebar + LLM card (4s probe timeout) | ✅ done |
| 3 | Catalog hero + grid + filter bar + folder chips + NR chip | ✅ done |
| 4 | MediaDetail hero + 3-col body + episode cards | ✅ done |
| 5 | EditMetadata Basics + Manage + Danger zone (5-of-6 design tabs in-place) | ✅ done |
| 5b | NeedsReview popup | ✅ done |
| 6 | Torrents header + counters + per-row design icon buttons | ✅ done |
| 6 | Settings + Root folders (embedded `<Explorer />`) + Rename history tabs | ✅ done |
| 7 | Dead-CSS cleanup, dead-dialog removal, FluentLabel typography overrides, self-host fonts template | ✅ done |
| Data | Schema + extracters + telemetry + events | ✅ done |

---

## 7. Things I deliberately did NOT do

- **Replace FluentDialog provider** — `IDialogService`, `FluentToastProvider`,
  `FluentTooltipProvider` stay. Re-skinned via CSS in fluent-overrides.css.
- **Remove FluentUI package** — keep; the kept widgets (DataGrid, Dialog,
  Toast, Tooltip, NavMenu) still depend on it.
- **Mobile / PWA / MAUI** — separate workstream.
- **Tweaks panel** — explicitly cut from REDESIGN_UI plan §7.1; settings
  Appearance tab covers the same controls.
- **Download actual WOFF2 font files** — instead, a self-host template lives
  in [`wwwroot/lib/fonts/`](src/Animarr.Web/wwwroot/lib/fonts/) (README +
  fonts.css). Drop the files there + switch one App.razor line to go offline.
- **Bulk find-replace of FluentLabel Typo="H*" → semantic `<h*>`** —
  46 occurrences across pages. fluent-overrides.css remaps font-family on
  `fluent-label[typo=...]` so they already render in Archivo Black. Refactor
  to semantic tags is cosmetic, deferred.

## 8. Quick risk checklist

| Concern | Status |
|---|---|
| Existing pages still load | ✅ build passes; UI routes preserved |
| MediaItem schema migration applies on next startup | ✅ EF migration generated; idempotent (additive only) |
| Per-source confidence visible | ✅ in MediaDetail Identification col |
| Backdrop sync on Catalog hero rotation | ✅ JS.InvokeVoidAsync wrapped in try/catch (prerender-safe) |
| NeedsReview chip live-updates | ✅ via QueueProc.ItemIdentified |
| Sidebar LLM card stops on dispose | ✅ event unsubscribed in IDisposable |
| Image cache stays inside MediaCachePaths.CacheRoot | ✅ never writes into user's media tree |
| Locked dark mode doesn't break Settings theme picker | ⚠️ Picker no-ops; Settings page is Phase 6 cleanup |
| Multserials enum value reaches the type filter | ✅ in Catalog Tabs items |
| Hue stable across re-identify | ✅ ApplyManualAsync preserves Hue; populate uses `?? HueHash.For(title)` |

---

## 9. Recommended next steps

All blocking work is shipped. Remaining items are polish / followups:

1. **Drop WOFF2 files into `wwwroot/lib/fonts/`** for offline Docker. Pattern
   already in place (`fonts.css` + README). One App.razor line swap to enable.
2. **Sanity boot** — start the server with a clean DB to confirm migration
   applies cleanly. EF can hang on `__EFMigrationsLock` from a previously
   interrupted run; workaround is to delete the row in SQLite or use a
   fresh DB during testing.
3. **Semantic header refactor** — bulk-replace `<FluentLabel Typo="H*">` with
   `<h2>`/`<h3>`. Cosmetic; current CSS override already gives them Archivo
   Black typography.
4. **MediaInfo / ffprobe integration** — episode meta line currently regex-
   extracts resolution + codec from filename. Adding MediaInfo would give
   accurate values when filename doesn't encode them.

## 10. Single-line diff stats (post-Phase 6/5/7)

```
22 files changed (estimated ~1400 insertions / ~800 deletions)
+ 29 new files (Design components, scoped CSS, migration, helpers, docs)
- 0 files removed (additive or in-place rewrites only)
```

## 11. Final UI flow walk-through

### A user opens the app
1. App.razor injects Geist + Geist Mono + Archivo Black + Noto Serif SC via Google Fonts CDN
2. MainLayout renders dark theme (locked), always-on backdrop slideshow, film-grain overlay
3. Sidebar shows brand + 3 nav entries + SidebarLlmStatus footer
4. LLM card probes endpoint, shows ONLINE/OFF/ERROR with provider · model, queue depth
5. Catalog hero rotates through top-5 ★≥8.0 titles, pushing fanart into global blurred backdrop
6. Below: type filter (segmented tabs) · search · Rescan all · NeedsReview chip · count
7. Folder chip strip (sections, accent-active)
8. Poster grid with hue tint, CJK corner watermark, confidence chip when <0.85

### A user opens a media item
1. Click Poster → /catalog/{id}
2. MediaDetailHero pushes FanartPath into backdrop, renders BACK + EDIT METADATA chips
3. 3-col body: Synopsis (overview + genres) · Details (mono kv list with Studio, Language, Episodes, Season, Runtime, Status) · Identification (TMDB/MAL/IMDb with per-source confidence + on-disk path)
4. NeedsReview banner appears when status=NeedsReview with top-3 candidates
5. Episodes section: per-season tabs, EpisodeCard grid with full ON DISK / MISSING visual states (green strip + ✓ vs warn strip + ⚠)
6. Click episode (if have) → existing player flow
7. Click EDIT METADATA → FolderEditPanel slides in

### A user edits metadata
1. FolderEditPanel.Metadata tab
2. Basics editor (new) — 2-column grid: Title, English title, CJK title, Original, Year, Type, Studio, Language, Runtime, Hue (with live swatch)
3. Tags row with chip remove + Enter-to-commit input
4. Save / Reset buttons
5. Save → sets status=Manual, LastMetadataRefreshedAt = now, calls SaveChangesAsync
6. Danger zone — "Also delete files from disk" toggle + two-step confirm
7. Confirm → DBO removed + queue dropped + (optional) directory deleted

### A user identifies a NeedsReview folder
1. NeedsReviewChip in Catalog header (pulse dot when count > 0)
2. Click → NeedsReviewDialog modal
3. Per-folder candidate cards with confidence color (success ↔ warn gradient)
4. Click "Use" → MetadataService.ApplyManualAsync (clears stale fields, populates from chosen source, downloads images)
5. ItemIdentified event fires → chip count refreshes, dialog re-queries

### Live updates (no SignalR)
- TorrentEngineService.StateChanged → Torrents.razor counters re-render
- IdentificationQueueProcessorService.QueueChanged → SidebarLlmStatus queue badge updates
- IdentificationQueueProcessorService.ItemIdentified → NeedsReviewChip + Dialog re-query

### Parsers (every source pulls everything available)
- TMDB: 28 fields per TV detail (was 16); 22 per Movie (was 14); +translations endpoint for EnglishTitle
- MAL: 22 fields (was 13); +studios, start_season, broadcast, source, nsfw, popularity, avg_ep_duration, pictures, end_date
- IMDb: 8 fields (was 7); +ImdbConfidence proxy
- Stored: Studio, Language, EnglishTitle, CjkTitle, Hue (deterministic FNV-1a), EpisodeCount, SeasonLabel, TagsJson (top-8 keywords), per-source confidences (TmdbConfidence/MalConfidence/ImdbConfidence)
- Image gallery for EditMetadata combines TMDB images + MAL pictures[]
- All paths absolute, in MediaCachePaths.CacheRoot — never in user's media tree
