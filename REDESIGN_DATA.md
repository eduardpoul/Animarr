# Animarr — Data Model & Navigation Migration Plan

> 2026-05-22. Companion to [REDESIGN_UI.md](REDESIGN_UI.md). That doc handles
> the visual layer (components, tokens, primitives). This one handles the
> **data contract** — fields the new design needs, IA changes, backend
> service hooks, and live-update channels.
>
> Authoritative source: [`design_handoff_animarr/NAVIGATION_AND_DATA.md`](design_handoff_animarr/NAVIGATION_AND_DATA.md).

---

## 0. TL;DR

- **Navigation collapses 5 → 3 sidebar destinations.** `/explorer` and `/history` move under `Settings`.
- **9 new fields** required on `MediaItem` + `FolderWatcher` to back the new hero, posters, detail page and section folder cards.
- **TMDB / MAL ingestion** doesn't currently extract `studio` or `language` — add to the response classes and the identify flow.
- **No REST API needed for the web app.** The contract document lists `/api/*` endpoints because the prototype is React; Blazor Server pages can read DbContext directly. REST surface is deferred to whenever PWA/MAUI happens.
- **SignalR isn't a goal** — the existing service-event pattern (DI singleton + `event`) gives us the same push-update behavior with one less moving part.

---

## 1. Navigation: what moved where

The design dropped two sidebar entries. Mapping old → new:

| Old surface | New home | Status |
|---|---|---|
| `/explorer` page | `Settings → Root folders` (CRUD) + catalog folder-chip filter | redirect /explorer → /settings/root-folders |
| `/explorer` NeedsReview banner | Catalog filter bar `NEEDS REVIEW · N` chip → modal | new UI surface |
| `/explorer` per-section inline tree | `MediaDetail` episodes grid | already there |
| `/explorer` Rescan/Identify/Edit/Delete actions | Catalog: global `Rescan all`; per-title actions in `EditMetadata → Manage` | new UX |
| `/explorer` "Delete with files" toggle | `EditMetadata → Manage → Danger zone` | new UX |
| `/history` page | `Settings → Rename history` (nested tab) | redirect /history → /settings/rename-history |
| `/folders`, `/section/{id}`, `/browse`, `/patterns` | already thin redirects per [REDESIGN.md §2.1](REDESIGN.md) | done |

**Sidebar additions:**
- Footer status card: `LLM · ONLINE · ollama · qwen2.5:1.5b · queue 17/25`. Reads from `IdentificationQueueProcessorService` + `MicrosoftAiLlmService`.

**Sidebar removals** ([MainLayout.razor:42-45](src/Animarr.Web/Components/Layout/MainLayout.razor)):
- `FluentNavLink Href="/explorer"` — delete (Phase 2.1 of REDESIGN_UI)
- `FluentNavLink Href="/history"` — delete (Phase 2.1)

**Route reshuffling** ([Components/Pages/](src/Animarr.Web/Components/Pages/)):
- `Explorer.razor`: keep as `/explorer` for now (deep links), but render the `Settings → Root folders` view OR set up redirect. Until Phase 6 ships the new Settings, keep the existing UI accessible at the legacy URL.
- `History.razor`: same — keep accessible, plan move into Settings tab.
- `Folders.razor`, `SectionFolders.razor`, `Browse.razor`, `Patterns.razor`: already thin-redirect stubs ([REDESIGN.md §2.1](REDESIGN.md)) — leave alone.

---

## 2. Data-model gap analysis

### 2.1 `MediaItem` — fields the design needs

Cross-referencing the design contract (`NAVIGATION_AND_DATA.md §3`) against [`Data/Models/MediaItem.cs`](src/Animarr.Web/Data/Models/MediaItem.cs):

| Design field | Current state | Action |
|---|---|---|
| `id` | `Guid Id` ✅ | — |
| `title` | `Title` ✅ | — |
| `cjk` | ❌ closest analogue is `OriginalTitle` but semantics differ (OriginalTitle can be romanji/transliteration; cjk specifically means CJK script) | **add `CjkTitle string?`** |
| `englishTitle` | ❌ | **add `EnglishTitle string?`** |
| `year` | `Year` ✅ | — |
| `type` | `MediaType` enum — missing `Multserials` value | **add `MediaItemType.Multserials = 4`** |
| `hue` (poster tint) | ❌ | **add `Hue int?`** (0..360). Used by Poster wash + CJK watermark color. |
| `bd` (backdrop URL) | `FanartPath` ✅ (served via `/api/image?path=…`) | — |
| `conf` (overall match confidence) | `LlmConfidence` ✅ | rename UI label only |
| `tmdb.confidence` / `mal.confidence` / `imdb.confidence` (per-source split, see contract §3.2 body) | ❌ — only overall `LlmConfidence` | **add `TmdbConfidence`, `MalConfidence`, `ImdbConfidence` doubles** (nullable). Falls back to LlmConfidence when null. |
| `episodes` (count) | derived from `SeasonsJson` (parsed on read) | **add `EpisodeCount int?`** denormalized. Cheaper for grid rendering of 200+ posters. |
| `season` (display label like "TV-1", "S2") | derived | **add `SeasonLabel string?`** — explicit override; falls back to format based on type. |
| `rating` | `Rating` ✅ | — |
| `runtime` | `Runtime` int? ✅ (minutes) | — UI formats to "22m" / "2h27m" |
| `studio` | ❌ | **add `Studio string?`** |
| `lang` (original audio language) | ❌ | **add `Language string?`** (e.g. "Mandarin", "Japanese") |
| `overview` | `Description` ✅ | — UI label |
| `tags[]` (descriptive: "Donghua", "Cultivation") | exists as `MediaItemTag` M2M, but `MediaTag` has Color/SortOrder/IsAutoTag (collection-tag semantics, not descriptive) | **add `TagsJson string?`** — simple denormalized string array on MediaItem. Keep MediaTag M2M dormant for a potential "user collections" feature later. |
| `posterUrl` (editable, set by EditMetadata) | `PosterPath` ✅ (file path; served via `/api/image`) | — |

**Total: 9 new fields on `MediaItem`, 1 enum value.**

### 2.2 `FolderWatcher` — fields the design needs

Design contract (§3.4 Root folders + §3.1 folder chips):

| Design field | Current state | Action |
|---|---|---|
| `id` | ✅ | — |
| `title` | `Label` ✅ | UI label maps |
| `path` | ✅ | — |
| `watchers` (count of child folders) | derived | computed on read — no schema change |
| `identified` (count) | derived | computed |
| `missing` (count) | derived | computed |
| `hue` (section card tint) | ❌ | **add `Hue int?`** |
| `bd` (section card backdrop image) | ❌ — only child MediaItems have FanartPath | **add `BackdropPath string?`** — relative file path inside the section; falls back to first child MediaItem's FanartPath when null. |

**Total: 2 new fields on `FolderWatcher`.**

### 2.3 Episodes — design vs reality

Design contract (§3.2 episodes grid) wants per-episode:
```
n, title, have (bool), runtime, resolution, codec, size, filePath
```

Current state:
- `MediaItem.SeasonsJson` only stores **season-level** summary (no per-episode rows).
- Per-episode data is **computed lazily** in [MediaDetail.razor:BuildEpisodeFileMapAsync](src/Animarr.Web/Components/Pages/MediaDetail.razor) by scanning the folder + applying patterns.
- `resolution`, `codec`, `size` would need MediaInfo / ffprobe parsing (not implemented).

**Decision:** keep the lazy approach. Don't persist per-episode rows.
- `have`, `filePath`, `size` come from file system scan.
- `title` (episode name) comes from TMDB on-demand fetch + lightweight in-memory cache.
- `resolution`, `codec` — defer. The design's "1080p · H.265 · 0.84 GB" meta line can degrade gracefully: show what we know, omit the rest. Add a follow-up issue for MediaInfo integration.

### 2.4 Identification per-source split

Design contract §3.2 body shows:
```
identification: { tmdb: {id, confidence}, mal: {id, confidence}, imdb: {id, confidence} }
```

Currently:
- IDs are persisted: `TmdbId`, `MalId`, `ImdbId`, `TvdbId`
- Confidence is a single `LlmConfidence` (the LLM's overall match score)

Per-source confidence is a UX nicety, not a correctness requirement. Two options:

**Option A (simple):** show `LlmConfidence` next to every source. Same number everywhere.
**Option B (proper):** add `TmdbConfidence`, `MalConfidence`, `ImdbConfidence`. Each source's `Search()` call returns a confidence score (typically `voteAverage` / `popularity` normalized) — persist that.

Go with **A first**, ship the UI, then add B as a follow-up. The plan below assumes A.

### 2.5 Hue assignment

The new poster/section visual identity depends on per-item `Hue` (0..360 degrees). Where does the value come from?

**Heuristic:** deterministic hash of `Title` modulo 360 → stable color per title without manual curation. Saves the user from picking colors for every show.

```csharp
public static int HueFor(string title) =>
    (int)((uint)title.GetDeterministicHash() % 360);
```

Refinement: a sparse mapping table for the common types — Donghua → red cluster, Mecha → blue cluster, etc. — so the catalog visually groups. Out of scope for the data plan; can be a post-launch tuning step.

For `FolderWatcher.Hue`: assign on create (hash of `Label`) or expose in the edit dialog.

---

## 3. TMDB / MAL ingestion gaps

Per [TmdbClient.cs:185-238](src/Animarr.Web/Services/TmdbClient.cs), the TmdbTvDetail and TmdbMovieDetail response classes **don't deserialize** `production_companies` or `original_language` even though TMDB returns them.

### 3.1 Add to `TmdbClient.cs`
```csharp
public class TmdbTvDetail {
    // … existing fields …
    [JsonPropertyName("production_companies")] public List<TmdbCompany> ProductionCompanies { get; set; } = [];
    [JsonPropertyName("original_language")]    public string? OriginalLanguage { get; set; }
    [JsonPropertyName("networks")]             public List<TmdbCompany> Networks { get; set; } = [];
}
public class TmdbMovieDetail {
    // … existing fields …
    [JsonPropertyName("production_companies")] public List<TmdbCompany> ProductionCompanies { get; set; } = [];
    [JsonPropertyName("original_language")]    public string? OriginalLanguage { get; set; }
}
public class TmdbCompany {
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
```

`original_language` is an ISO-639-1 code ("ja", "zh", "en"). Map to display name client-side via a small lookup table (Mandarin, Japanese, English, Korean — the meaningful subset for an anime/donghua library).

### 3.2 Studio extraction logic in `MetadataService.cs`
- For TV: prefer `Networks[0].Name` ("HBO", "Bilibili", "Tencent Penguin") over `ProductionCompanies[0].Name`. Networks is what users recognize.
- For Movie: use `ProductionCompanies[0].Name` (Paramount, Coloroom, Apple).
- For Anime: TMDB rarely has studio data; fall back to MAL's `studios[0].name`. MAL's Jikan API exposes this — check if [MalClient.cs](src/Animarr.Web/Services/MalClient.cs) already pulls it.

### 3.3 MAL studio field

Need to verify MAL's `Jikan/v4/anime/{id}` response is being parsed for `studios`. If not, add. (Will check during Phase implementation, listed as a verification step below.)

---

## 4. NeedsReview surface

The data is **already in the database** — every `MediaItem` with `IdentificationStatus.NeedsReview` has `CandidatesJson` with top-3 candidates (Phase 2.3 of REDESIGN.md). What's missing is the **catalog-level surface** for it.

Design contract §3.1.4: chip in filter bar → modal listing folders with candidate cards.

Today:
- ✅ Data: `MediaItem.IdentificationStatus == NeedsReview` + `CandidatesJson`
- ✅ Banner exists on `MediaDetail` (per-title)
- ❌ Catalog-level chip + modal

**New UI:**
1. Filter bar chip on Catalog (Phase 3 of REDESIGN_UI) — count from `db.MediaItems.Count(m => m.IdentificationStatus == NeedsReview)`.
2. Modal listing each item: folder name (`MediaItem.Folder.Label`), candidates from `CandidatesJson`, `Use` button → existing `MetadataService.ApplyManualAsync` (already implemented per Phase 2.3).

**No new backend needed** — wire the existing pieces together.

---

## 5. Live updates (the "SignalR" section)

The design contract talks about SignalR hubs (because the prototype is React). In Blazor Server we get the same push-update behavior via DI singletons + `event` callbacks. **No SignalR hub registration is needed.**

| Contract hub channel | Blazor-equivalent | Status |
|---|---|---|
| `TorrentHub.Counters` | `TorrentEngineService.GlobalStatsChanged` event | needs hook |
| `TorrentHub.TorrentUpdated` | `TorrentEngineService.TorrentUpdated` event | partially there — verify |
| `TorrentHub.TorrentAdded` / `Removed` | DB write + `StateHasChanged()` on subscriber | works today |
| `IdentificationHub.QueueChanged` | `IdentificationQueueProcessorService.ProgressChanged` event | needs hook |
| `IdentificationHub.ItemIdentified` | `MetadataService.ItemIdentified` event | needs hook |
| `IdentificationHub.NeedsReviewChanged` | derived — re-query when ItemIdentified fires | works today |
| `HistoryHub.EntryAdded` / `Reverted` | `RenameService.HistoryChanged` event | needs hook |

**Action:** add these C# events to the relevant services. Razor pages subscribe in `OnInitialized` and unsubscribe in `IDisposable`. No protocol overhead.

If we later ship a MAUI/PWA client, **then** we wrap these events behind a SignalR hub. Until then, in-process events are simpler.

---

## 6. AI/LLM telemetry

Design contract §3.4 AI/LLM tab wants:
```
{ status, queue: {processed, total}, avgLatencyMs, hitRate }
```

Current state:
- ✅ Config (provider, model, baseUrl, apiKey) — in AppConfig
- ✅ Queue depth — countable from `IdentificationQueue` table
- ❌ Status probe (online/offline)
- ❌ Average latency
- ❌ Hit rate

**Plan:**
- **Status probe:** [MicrosoftAiLlmService.cs](src/Animarr.Web/Services/MicrosoftAiLlmService.cs) likely already does a "test connection" call. Wrap it as `Task<bool> ProbeAsync(CancellationToken)` callable from the LLM settings page.
- **Latency:** maintain a `ConcurrentQueue<int>` of last 50 call latencies in `MicrosoftAiLlmService`. `AverageMs` computed property.
- **Hit rate:** when the queue processor finishes an item, increment one of two counters: `Identified` (status ended `Identified|Manual`) or `Missed` (`NeedsReview|Failed`). Expose `HitRate => Identified / (Identified+Missed)`. Reset is a manual user action.

Storage: in-memory in the singleton service. Resets on app restart. That's fine — these are observability metrics, not data.

---

## 7. REST API — defer

Design contract enumerates `/api/library`, `/api/folders`, `/api/torrents`, `/api/history`, `/api/llm/*`, `/api/needs-review`, `/api/patterns`, `/api/ignores`, `/api/torrent/config`, `/api/metadata/sources`.

Currently the Blazor Server app uses **none** of these — pages read DbContext directly via `IDbContextFactory<AppDbContext>`. Only `/api/image` and `/api/video` exist ([Program.cs:135, 207](src/Animarr.Web/Program.cs)).

**Don't build the REST surface yet.** Reasons:
- Single-user self-hosted app — no auth, no other clients today.
- A REST API is a maintenance burden (versioning, validation, auth-someday) that earns its keep only with multiple consumers.
- Mobile (PWA / MAUI) is out of scope per [REDESIGN_UI.md §7.2](REDESIGN_UI.md).

**If/when** we ship the mobile client: build a thin REST layer on top of the existing services (one MapGet per existing service query method). No need to refactor pages — they keep their DbContext-direct path; only the REST layer is new.

---

## 8. Migration plan

Order so each step is shippable on its own.

### Step 1 — Schema additions (one EF migration)
Add the new fields. **No data backfill yet** — they remain null until step 2 populates them.

**`MediaItem`:**
```csharp
public string? CjkTitle { get; set; }
public string? EnglishTitle { get; set; }
public string? Studio { get; set; }
public string? Language { get; set; }
public int?    Hue { get; set; }
public int?    EpisodeCount { get; set; }
public string? SeasonLabel { get; set; }
public string? TagsJson { get; set; }
public double? TmdbConfidence { get; set; }
public double? MalConfidence { get; set; }
public double? ImdbConfidence { get; set; }
```

**`MediaItemType`:** add `Multserials = 4`.

**`FolderWatcher`:**
```csharp
public int?    Hue { get; set; }
public string? BackdropPath { get; set; }
```

Migration name: `AddDesignFieldsToMediaItemAndFolder`. Trivial — `dotnet ef migrations add ...`.

### Step 2 — TMDB / MAL ingestion extends
- [TmdbClient.cs](src/Animarr.Web/Services/TmdbClient.cs): add `ProductionCompanies`, `Networks`, `OriginalLanguage` to `TmdbTvDetail` and `TmdbMovieDetail` + nested `TmdbCompany` class.
- [MalClient.cs](src/Animarr.Web/Services/MalClient.cs): verify `studios[]` is parsed; add if missing.
- [MetadataService.cs](src/Animarr.Web/Services/MetadataService.cs): in the identify pipeline, after picking a match, set on the MediaItem:
  - `Studio` ← Networks[0] / ProductionCompanies[0] / MAL studios[0] (priority order per §3.2)
  - `Language` ← lookup table on `OriginalLanguage` ISO code
  - `EpisodeCount` ← sum from `Seasons` summary (or set per-season-1 count)
  - `SeasonLabel` ← `"TV-1"` for anime/donghua, `"S{n}"` for series, omit for movies
  - `Hue` ← `HueFor(Title)` deterministic hash on first identify (preserved on re-identify)
  - `CjkTitle` ← if `OriginalLanguage` ∈ {"zh", "ja", "ko"} and `OriginalTitle` contains CJK characters, copy to CjkTitle
  - `EnglishTitle` ← search candidates' English-title alternative if available

### Step 3 — NeedsReview Catalog surface (UI-only, no schema)
- Catalog filter bar chip (Phase 3 of [REDESIGN_UI.md](REDESIGN_UI.md)) reads `db.MediaItems.Count(m => m.IdentificationStatus == NeedsReview)`.
- Modal queries `db.MediaItems.Where(m => m.IdentificationStatus == NeedsReview).Include(m => m.Folder)`. For each item, deserialize `CandidatesJson` → list cards.
- `Use` button calls existing `MetadataService.ApplyManualAsync(itemId, candidate)`.
- Live refresh: subscribe to `MetadataService.ItemIdentified` event (added in step 6) and re-count.

### Step 4 — Navigation collapse
- [MainLayout.razor:42-45](src/Animarr.Web/Components/Layout/MainLayout.razor): remove the `/explorer` and `/history` `<FluentNavLink>` entries.
- Settings page (Phase 6 of REDESIGN_UI): add Root folders and Rename history tabs that **mount the existing `Explorer.razor` and `History.razor` content** as embedded child components. Don't duplicate the logic — refactor each page into `Explorer.razor` thin wrapper + `<ExplorerView />` reusable component.
- Keep `/explorer` and `/history` as direct routes still, so deep links work — they render the same wrapped content.

### Step 5 — Sidebar LLM status card
- New `<LlmStatusCard />` component bound to `MicrosoftAiLlmService` + `IdentificationQueueProcessorService`.
- Slot into bottom of `.animarr-sidebar` in [MainLayout.razor](src/Animarr.Web/Components/Layout/MainLayout.razor).
- Reads: `IsConfigured` (LlmEnabled config) → "ONLINE" / "OFF". Queue depth → "17/25" from `IdentificationQueue.Count()`. Provider + model from AppConfig.

### Step 6 — Service events (live updates without SignalR)
Add `event` declarations to existing singleton services:
- [TorrentEngineService.cs](src/Animarr.Web/Services/TorrentEngineService.cs): `GlobalStatsChanged`, `TorrentUpdated` events.
- [IdentificationQueueProcessorService.cs](src/Animarr.Web/Services/IdentificationQueueProcessorService.cs): `ProgressChanged`, `ItemIdentified`, `QueueChanged` events.
- [RenameService.cs](src/Animarr.Web/Services/RenameService.cs): `HistoryChanged` event.

Subscribers (Catalog, Torrents, Settings panels) call `InvokeAsync(StateHasChanged)` on event fire. Unsubscribe in `Dispose`.

### Step 7 — Catalog folder filter chip row
- Below the type tabs, render a chip row: All + every `FolderWatcher` where `IsSection==true`. Chip is `<button class="chip" style="--accent-hue:{folder.Hue}">{folder.Label}</button>`.
- Folder filter combines with type filter via AND.
- Wired to the `MediaItem.FolderId → FolderWatcher.ParentSectionId` chain.

### Step 8 — LLM telemetry
- Latency rolling window in `MicrosoftAiLlmService`.
- Hit-rate counters in `IdentificationQueueProcessorService`.
- Settings → AI/LLM hero card reads these.

### Step 9 — Edit Metadata drawer fields
This is largely UI work (Phase 5 of REDESIGN_UI) but the data hookups matter:
- `Basics` tab edits `Title`, `CjkTitle`, `EnglishTitle`, `Year`, `MediaType`, `Language`, `Studio`, `Runtime` directly on the MediaItem entity.
- `Tags` tab edits `TagsJson` (split/join on comma).
- `Source IDs` tab edits `TmdbId`, `MalId`, `ImdbId`. "Paste URL" parses TMDB/MAL URLs via regex client-side.
- `Manage` tab actions all map to existing service methods (`Rescan`, `Reidentify`, `RevertAll` via `RenameService`, etc.) — wire only.

---

## 9. Out of scope (named for completeness)

These appear in the design contract but are intentionally deferred:

| Item | Why deferred |
|---|---|
| REST API (`/api/library`, `/api/folders`, …) | No second client today. Build when MAUI/PWA arrives. |
| SignalR hub registration | In-process events do the same job. Migrate if multi-client. |
| MediaInfo / ffprobe per-episode `resolution`, `codec`, `size` | Existing MediaDetail UX shows `size` from `FileInfo.Length`. Resolution/codec require an extra binary or library — separate issue. |
| Per-source confidence (`tmdb.confidence`, `mal.confidence`) | Step 1's columns are added but null until Step 2 is enhanced. UI shows shared `LlmConfidence` first. |
| AniDB integration | Mentioned in design under metadata sources; no existing client. Add later. |
| MediaTag/MediaItemTag (collections) | Existing tables stay dormant. Descriptive tags use new `TagsJson` instead. |
| Continue-watching / playback progress | Out of scope per the design itself (§6 of contract). |
| Multi-user / auth | Out of scope (single-user self-hosted). |

---

## 10. Verification checklist

After steps 1-2 ship:
- [ ] Re-identify a Donghua title → `Studio`, `Language`, `Hue`, `CjkTitle` populated
- [ ] Re-identify a TMDB Series → `Studio` = network name, `Language` = "Japanese"/"English" mapped from ISO
- [ ] Re-identify a Movie → `Studio` = production company, `EpisodeCount` null, `SeasonLabel` null
- [ ] `MediaItemType.Multserials` selectable in the type filter
- [ ] `FolderWatcher.Hue` defaults to deterministic hash; editable in Settings → Root folders
- [ ] Old `/explorer` and `/history` URLs still render (Step 4 keeps routes)
- [ ] NeedsReview chip count updates live when LLM finishes processing an item (Step 3 + Step 6)
- [ ] Sidebar LLM card shows queue depth advancing during a rescan (Step 5 + Step 6)
- [ ] Existing tests pass (no removed fields, only additions)
