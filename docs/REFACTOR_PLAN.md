# Animarr Architecture Refactor: Server → API + Shared UI + WASM + MAUI

Status: **Plan approved**, execution starting Phase 0.
Branch: `feature/arch-split` (forks from `redesign/ui-migration` head).
Owner: see `git log --author`.

## Goal

Split the current monolithic Blazor Server app into:

1. **`Animarr.Web`** — pure HTTP API (current project, gutted of Razor)
2. **`Animarr.Shared`** — DTOs, route constants, interface definitions
3. **`Animarr.UI`** — Razor Class Library with ALL pages and components
4. **`Animarr.Web.Client`** — Blazor WebAssembly client (uses `Animarr.UI`)
5. **`Animarr.App`** — .NET MAUI Blazor Hybrid (Android + iOS + Windows + macOS, uses `Animarr.UI`)

UI and functionality must remain **identical** through the migration. Auth stays open (no auth yet).

### Confirmed decisions

| Decision | Choice |
|---|---|
| Real-time updates | SignalR hub |
| MAUI targets | Full set: Android + iOS + macOS + Windows |
| WASM hosting | Same container as API (single deployment artifact) |
| Approach | Detailed plan first, then phase-by-phase |
| iOS/macOS testing | Not available (no Mac) — code targets compile but won't be runtime-tested by us |

## Final solution layout

```
Animarr.sln
├── src/
│   ├── Animarr.Shared/              <-- NEW. DTOs, interfaces, enums, route constants.
│   ├── Animarr.UI/                  <-- NEW. Razor Class Library: all pages + components + styles.
│   ├── Animarr.Web/                 <-- EXISTING. API-only after Phase 5. Hosts WASM bundle.
│   ├── Animarr.Web.Client/          <-- NEW. Blazor WebAssembly entry.
│   └── Animarr.App/                 <-- NEW. MAUI Blazor Hybrid (Android/iOS/Win/macOS).
├── deploy/                          (unchanged)
└── docs/
    ├── REFACTOR_PLAN.md             (this file)
    └── REFACTOR_ACCEPTANCE.md       (Phase 0 output)
```

## Phase 0 — Baseline + acceptance criteria (1 day)

**Goal:** lock down what "works today" so we have a regression target.

- [ ] Create `feature/arch-split` branch from current `redesign/ui-migration` HEAD
- [ ] Commit current player work to baseline (or merge first)
- [ ] Write `docs/REFACTOR_ACCEPTANCE.md` — per-page checklist of behaviors that must keep working
- [ ] Scaffold (empty but valid) `Animarr.Shared`, `Animarr.UI`, `Animarr.Web.Client`, `Animarr.App` projects
- [ ] Add all four to `Animarr.sln`
- [ ] `dotnet build` succeeds against the whole solution

## Phase 1 — Animarr.Shared + API surface design (3 days)

**Goal:** define the complete HTTP contract (DTOs + route map + client interface).

### Files

- `src/Animarr.Shared/Animarr.Shared.csproj` → `net10.0`, no platform deps
- `src/Animarr.Shared/Api/ApiRoutes.cs` → const route strings (~70)
- `src/Animarr.Shared/Api/IAnimarrApiClient.cs` → single interface, methods grouped by area
- `src/Animarr.Shared/Dtos/*.cs` → ~30 DTO records
  - Catalog, Media, Torrent, WatchState, RootFolder, AppConfig, MetadataSearchResult,
    TmdbDetails, MalDetails, IdentificationQueue, LlmStatus, HardwareReport,
    Pattern, IgnoreRule, RenameHistoryEntry, etc.
- `src/Animarr.Shared/Realtime/*.cs` → SignalR event contracts
  - `TorrentProgressEvent`, `IdentificationStartedEvent`, etc.

### Done when

`Animarr.Web` builds with `Animarr.Shared` referenced; no functional change.

## Phase 2 — API endpoints + SignalR hubs (4 days)

**Goal:** add every endpoint the UI needs that doesn't exist yet, plus real-time hubs.

### Endpoint groups (new in `src/Animarr.Web/Endpoints/`)

| Group | Routes | Count |
|---|---|---|
| Catalog | `/api/catalog`, `/{id}`, `/{id}/identify` | 5 |
| Media | `/api/media/{id}`, edit metadata | 4 |
| Torrents | `/api/torrents` (CRUD + control) | 8 |
| WatchState | `/api/watch-state/*` | 3 |
| Config | `/api/config` (CRUD AppConfig) | 3 |
| Metadata | `/api/metadata/search`, `/details` (TMDB/MAL proxy) | 4 |
| Identification | `/api/identification/queue` | 3 |
| LLM | `/api/llm/status`, `/test` | 2 |
| Folders | `/api/folders/root` (CRUD) | 5 |
| Patterns | `/api/patterns` (CRUD) | 5 |
| IgnoreRules | `/api/rules` (CRUD) | 4 |
| History | `/api/history` | 2 |

Plus existing endpoints (HLS, DLNA, file, image, hardware) — keep as-is, just relocate into endpoint-group classes for organization.

### SignalR hubs (new in `src/Animarr.Web/Hubs/`)

- `TorrentHub` → `/hubs/torrents`. Pushes progress, state changes.
- `IdentificationHub` → `/hubs/identification`. Pushes queue events.

### Program.cs changes

- `builder.Services.AddSignalR();`
- `app.UseCors(...)` policy for WASM client (allow same-origin since we're hosting bundle together; also allow MAUI client which will use the public host)
- `app.MapHub<TorrentHub>("/hubs/torrents");`
- `app.MapCatalog();` etc.

### Done when

- All endpoints respond correctly via curl
- SignalR client can subscribe and receive events from a test harness
- Existing Blazor Server pages still work (we haven't touched UI yet)

## Phase 3 — Animarr.UI RCL (7 days)

**The biggest phase.** Split into sub-phases.

### Phase 3.1 — Scaffold RCL + platform abstractions (1 day)

- `src/Animarr.UI/Animarr.UI.csproj` → `net10.0`, `Microsoft.NET.Sdk.Razor`
- `Services/IStorageService.cs` — abstraction over `localStorage` / `Preferences` / `SecureStorage`
- `Services/IPlatformService.cs` — abstraction over file download, sharing, native cast
- `Services/IFormFactorService.cs` — abstraction over Mobile/Tablet/Desktop/Tv detection
- `Services/AnimarrApiClient.cs` — HttpClient-based impl of `IAnimarrApiClient`
- `Services/ClientThemeService.cs`, `ClientLocalizationService.cs`, `ClientAppConfigService.cs`
- `Realtime/TorrentHubClient.cs` — wraps `HubConnection`

### Phase 3.2 — Move design components (1 day)

Move-and-replace from `Animarr.Web/Components/Design/` to `Animarr.UI/Components/`:
- Primitives (Input, Toggle, Pill, Tabs, Select, Spinner, Skeleton, Btn, DIcon)
- Media (Poster, CatalogHero, EpisodeCard, MovieFileCard, MediaDetailHero)
- Fluent shims
- Loaders

These have no server deps, should compile immediately.

### Phase 3.3 — Refactor pages one at a time (5 days)

Order from simplest to hardest:

1. `History.razor` — DB readonly → `apiClient.GetHistoryAsync()`
2. `RootFoldersList.razor` — CRUD AppConfig + folder watcher → API
3. `Settings.razor` — bigger but mostly CRUD; refactor `IWebHostEnvironment` away
4. `Torrents.razor` — add SignalR client for real-time updates
5. `Home.razor` — catalog query → API; preserve filter state
6. `MediaDetail.razor` — biggest; TMDB/MAL/LLM/WatchState all via API
7. `TorrentEdit.razor`, `EditMetadataDrawer.razor` — drawer subroutes

Each refactored page validated by running it in Blazor Server (via temporary `Animarr.Web` reference to `Animarr.UI`) before moving on.

### Phase 3.4 — Client-side state services (1 day)

- `ClientThemeService` — `IStorageService`-backed cache, GET `/api/config/theme` initial load
- `ClientLocalizationService` — same pattern for language strings
- `ClientAppConfigService` — thin HTTP wrapper, used by Settings page

### Done when

All 18 pages compile + render inside `Animarr.UI`, no server-side DI usage remaining.

## Phase 4 — Animarr.Web.Client WASM (3 days)

**Goal:** Blazor WebAssembly project hosting `Animarr.UI`.

### Files

- `src/Animarr.Web.Client/Animarr.Web.Client.csproj` → `Microsoft.NET.Sdk.BlazorWebAssembly`
- `Program.cs` — bootstrap, DI:
  - `HttpClient` with `BaseAddress = builder.HostEnvironment.BaseAddress`
  - `IAnimarrApiClient` → `AnimarrApiClient`
  - `IStorageService` → `BrowserStorageService` (localStorage via JSInterop)
  - `IPlatformService` → `BrowserPlatformService`
  - `IFormFactorService` → `BrowserFormFactorService` (window.innerWidth → factor)
  - `ClientThemeService`, `ClientLocalizationService`, `ClientAppConfigService` as singletons
  - SignalR `HubConnectionBuilder` for torrent + identification hubs
- `wwwroot/index.html` — entry HTML; copies CDN scripts (Artplayer, hls.js) from current `App.razor`
- `Services/BrowserStorageService.cs`
- `Services/BrowserPlatformService.cs`
- `Services/BrowserFormFactorService.cs`

### Done when

`dotnet run --project Animarr.Web.Client` opens the WASM client in a browser; every page loads from the API and behaves identically to current Blazor Server.

## Phase 5 — Switch Animarr.Web to API-only (2 days)

### Changes

- Delete `Animarr.Web/Components/` directory (all Razor)
- `Program.cs` remove `AddRazorComponents`, `MapRazorComponents`
- Add `<ProjectReference>` to `Animarr.Web.Client` from `Animarr.Web.csproj`
- Add static file middleware + `MapFallbackToFile("index.html")` for SPA routing
- Dockerfile: nothing changes — `dotnet publish` already pulls WASM client into `wwwroot/`

### Done when

Single container builds + runs; opening `http://server:8080/` serves WASM client; opening `/api/*` hits API endpoints.

## Phase 6 — Animarr.App MAUI Hybrid (5 days)

### Files

- `src/Animarr.App/Animarr.App.csproj` — multi-targets:
  - `net10.0-android`
  - `net10.0-ios`
  - `net10.0-maccatalyst`
  - `net10.0-windows10.0.19041`
- `MauiProgram.cs`:
  - `MauiBlazorWebView`
  - `IStorageService` → `MauiStorageService` (`Preferences`/`SecureStorage`)
  - `IPlatformService` → `MauiPlatformService` (`FileSaver`, `Launcher`, `Share`)
  - `IFormFactorService` → `MauiFormFactorService` (Android TV detection, `DeviceIdiom`)
  - `ApiHostStore` — persists user-entered server URL via `Preferences`
- `MainPage.xaml` — single `BlazorWebView` root
- `Platforms/Android/MainActivity.cs`, manifest with `leanback` feature flag
- `Platforms/iOS/AppDelegate.cs`
- `Platforms/MacCatalyst/Program.cs`
- `Platforms/Windows/App.xaml.cs`

### Server URL entry UX

First launch: modal "Enter Animarr server URL". Saved to `Preferences`. If unreachable on subsequent launches, show modal again.

### Done when

- Android: APK builds + installs on emulator + real device
- Windows: MSIX builds + runs as desktop app
- iOS/macOS: code compiles for targets (not tested without Mac)

## Phase 7 — Android TV layout + DPad nav (3 days)

- `MauiFormFactorService` detects `UI_MODE_TYPE_TELEVISION` on Android
- Pass `FormFactor` via `CascadingValue` from `MainLayout`
- Add `--tv` modifier CSS classes for: catalog grid (bigger posters, fewer per row), settings (vertical pills), player chrome (larger buttons)
- All interactive elements get `tabindex="0"`, `:focus-visible` ring
- Test on Android TV emulator (avd with `tv_1080p` profile)
- `AndroidManifest.xml` declares `leanback` activity + banner

## Phase 8-10 — Windows / macOS / iOS targets (3-6 days)

- **Phase 8 (Windows MAUI, 1 day):** mostly free; test resize, native media element
- **Phase 9 (macOS, 2 days):** code only — can't test without Mac
- **Phase 10 (iOS, 5 days):** code only — needs Mac + Apple Developer account for build/sign/distribute

## Phase 11 — Deployment + docs (2 days)

- Dockerfile for Animarr.Web with WASM bundle baked in
- GitHub Actions workflows:
  - Build & push Docker image (Linux)
  - Build Android APK → artifact
  - Build Windows MSIX → artifact
  - macOS/iOS workflows on `macos-latest` runners (when needed)
- README updates: deployment per platform
- Migration notes

## Time estimate

| Phase | Days |
|---|---|
| 0 — Baseline | 1 |
| 1 — Shared + API design | 3 |
| 2 — API + SignalR | 4 |
| 3 — UI RCL (18 pages) | 7 |
| 4 — WASM | 3 |
| 5 — Switch Web | 2 |
| 6 — MAUI base | 5 |
| 7 — Android TV | 3 |
| 8 — Windows | 1 |
| 9 — macOS | 2 |
| 10 — iOS | 5 |
| 11 — Deployment + docs | 2 |
| **Total** | **~38 working days** |

## Risks

1. **Phase 3 grind** — 18 pages, each needs its own refactor. Easy to miss server deps.
2. **iOS without Mac** — code can target it but can't sign/install without Mac + Apple Developer.
3. **SignalR in MAUI WebView** — WebSocket support varies by platform; may need native client fallback.
4. **Theme/locale** — currently server-side singletons; moving client-side changes lifecycle (per-circuit → per-app).
5. **localStorage migration** — existing audio sync values must port from `localStorage` directly into `IStorageService` abstraction without losing user calibration.
6. **`MetadataService` (1,708 LOC)** — has implicit assumptions about running server-side (file system, DI scopes). Need careful API surface to expose its work without leaking server concerns.

## Branch / merge strategy

- All work on `feature/arch-split`, forked from `redesign/ui-migration` HEAD
- Phase 5 is the cutover commit — after that `Animarr.Web` no longer hosts UI
- Don't merge to main until Phase 5 + Phase 4 deliver feature-parity WASM client
- MAUI work (Phase 6-10) can land in main as separate PRs after the WASM cutover
