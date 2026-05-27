# Navigation & Data Contract

This document is the second half of the design handoff. It complements `README.md` by spelling out:
1. **How navigation changed** during the design iteration (what to remove, what to merge).
2. **The full sitemap** for both desktop v3 and mobile — every reachable surface.
3. **Per-block data requirements** — for every block of every page, what fields must be present, what API calls likely back them, and what state changes when the user interacts.

If you implement nothing else accurately, get this right — it's the contract between design and the SQLite schema / REST + SignalR surface.

---

## 1. Navigation: before → after

The original spec described **5 top-level destinations**: Catalog · Torrents · Explorer · History · Settings. During design review the user collapsed this to **3**: Catalog · Torrents · Settings. Two destinations were merged away.

### Removed: `Explorer` (a.k.a. Library)
**Reason:** the old Explorer was a hybrid of "where my watched folders live" (config) and "what's inside each folder right now" (browsing). Those are two different jobs in one tab.

**Now lives at:**
| Old Explorer feature                                                    | New home                                                                |
|-------------------------------------------------------------------------|-------------------------------------------------------------------------|
| List of section folders / FolderWatchers, paths, watcher counts         | **Settings → Root folders** (CRUD)                                       |
| Filter library "by folder"                                              | **Catalog** — folder-chip row below the type tabs                       |
| NeedsReview banner with candidate matches                               | **Catalog** — `NEEDS REVIEW · N` button in the filter row → opens modal |
| Inline file tree for a section                                          | **MediaDetail** episode grid (per identified title)                     |
| Rescan / Restore deleted / Identify / Edit / Delete section actions     | **Catalog** has global `Rescan all`; per-title actions live in **Edit Metadata → Manage** |
| Delete folder with optional "Also delete files from disk"               | **Edit Metadata → Manage → Danger zone**                                |
| Bulk-select + Apply renames                                              | **Edit Metadata → Manage → Apply pending renames** (with `Preview`)     |

### Removed: `Rename History` (top-level route)
**Reason:** it's reference / audit content, not a place users spend time. It belongs in config.

**Now lives at:** `Settings → Rename history`. Same content (timeline grouped by date, KPI cards, per-row revert) — just nested.

### Kept
- **Catalog** — unchanged role, expanded scope.
- **Torrents** — unchanged.
- **Settings** — unchanged role, expanded scope (now houses Root folders + Rename history alongside the existing General / Appearance / AI/LLM / Patterns / Ignore rules / Torrent / Metadata).

### Final sidebar / tab bar

```
Desktop sidebar           Mobile bottom tab bar
─────────────────         ─────────────────────
  Catalog                   Catalog
  Torrents      ←→ live     Torrents      ←→ live
  Settings                  Settings
```

Sidebar also has a footer "LLM · ONLINE" status card showing the current provider, model and queue depth.

---

## 2. Full sitemap

```
/  (Catalog)
├── hero (rotates through featured ★≥8.0 titles)
├── filter bar  (type tabs · search · Rescan all · NeedsReview chip · count)
├── folder chips row  (All + every SectionFolder.title)
├── grid of posters
│
├── popup: NeedsReview modal
│        (Folders that need a candidate picked. Triggered by NR chip in filter bar.)
│
└── pushed: MediaDetail/{id}
    ├── hero (poster + title + meta + actions)
    ├── body  (Synopsis · Details · Identification)
    ├── episodes grid (per season)
    └── drawer: EditMetadata
        ├── tab: Source IDs
        ├── tab: Basics
        ├── tab: Poster (gallery)
        ├── tab: Backdrop (gallery)
        ├── tab: Tags
        └── tab: Manage  (Rescan / Apply renames / Revert / Danger zone)

/torrents
├── header bar (counters, Add torrent button)
├── tab segment (Active / Queued / All)
├── live table or card list of torrents
└── drawer/sheet: Add torrent
    ├── source toggle (File / Magnet)
    ├── files list with per-file priority
    ├── destination (identified titles dropdown) + manual path
    ├── speed caps
    └── advanced toggles

/settings
├── tab: General             (language; read-only watcher params)
├── tab: Root folders        (CRUD over SectionFolder list — moved from Explorer)
├── tab: Rename history      (audit log — moved from /history)
├── tab: Appearance          (theme, accent swatches, backdrop sliders)
├── tab: AI / LLM            (status hero, provider/model/URL/key, test connection)
├── tab: Patterns            (regex table)
├── tab: Ignore rules        (glob table)
├── tab: Torrent             (ports, global caps, encryption / DHT / PEX / UPnP)
└── tab: Metadata            (TMDB / MAL / IMDb / AniDB order + status)
```

Mobile structure is identical except Settings tabs become individual sub-screens reached via `chev-r` rows from a list. See `mobile-app.jsx → MoreMobile` for the row → sub-screen pattern.

---

## 3. Data per page / per block

For each page, every block has:
- **Inputs** — fields read from the domain model
- **Source** — likely backend call(s) needed to populate it
- **Outputs / side effects** — what the user can trigger from this block

### 3.1 Catalog

#### Block: Hero

**Inputs**
```
featured: MediaItem[]   // rating >= 8.0, take first 5
   ↳ each item needs: id, title, cjk, type, tags[], year, rating, episodes,
     studio, language, overview, bd (backdrop URL), hue (degrees)
heroIndex: int           // rotates every tweaks.rotateSec
```

**Source**
- `GET /api/library/featured?limit=5&minRating=8` — or just `GET /api/library?orderBy=rating&take=5`. Project against `MediaItem` shape.
- For backdrops, the URL points at a TMDB / MAL / local image. Hand back a stable URL — the design cross-fades by changing the `src` only.

**Side effects**
- `setBdImage(featured[heroIndex].bd, .hue)` — pushes the current backdrop into the global state so the page-wide blurred backdrop matches.
- Hero CTA `Open detail` → navigates to MediaDetail for the current featured item.

#### Block: Filter bar

**Inputs**
```
typeFilter: "All" | "Anime" | "Movie" | "Series" | "Multserials"
folderFilter: "All" | <SectionFolder.title>
query: string
NEEDS_REVIEW.length: int      // count of folders awaiting a match
```

**Source**
- `GET /api/folders` — for the folder chip list. Need `title` only.
- `GET /api/needs-review/count` — cheap count for the badge; refresh on a SignalR `NeedsReviewChanged` event.

**Side effects**
- `Rescan all` → `POST /api/scan/all` (kicks off the watcher across all sections; updates progress over SignalR).
- `NEEDS REVIEW · N` chip → opens the NeedsReview modal (data block 3.1.4).

#### Block: Poster grid

**Inputs**
```
items: MediaItem[]   // filtered by type + folder + query
```

Each poster needs the **same fields** as the hero (id, title, cjk, type, year, rating, episodes, bd, hue) plus `conf` (0..1) so we can show a confidence chip when `< 0.85`.

**Source**
- `GET /api/library?type=…&folder=…&q=…&take=200`.
- For pagination at scale, infinite-scroll the grid; first page should be enough for typical (≈ 200 titles).

**Side effects**
- Click poster → MediaDetail.

#### Block: NeedsReview modal

**Inputs**
```
NEEDS_REVIEW: {
  id: string,
  folder: string,            // raw folder name from disk
  candidates: {
    title, year, source: "TMDB" | "MAL" | "IMDb",
    cjk, conf, hue
  }[]
}[]
```

**Source**
- `GET /api/needs-review` — returns the list above.
- Each `candidates[]` was produced by the LLM + TMDB/MAL/IMDb fetch.

**Side effects**
- `Use` button → `POST /api/needs-review/{id}/resolve { sourceId, source }`. Writes the chosen MediaItem mapping to SQLite, drops the entry from the list, broadcasts `NeedsReviewChanged` over SignalR.
- `Search manually` → opens a search picker (out of scope for this design; treat as a navigation to the existing Edit Metadata flow).

### 3.2 MediaDetail

#### Block: Hero

**Inputs**
```
item: MediaItem
   ↳ all fields used: id, title, cjk, type, tags[], year, rating, episodes,
     studio, runtime, language, overview, conf, bd, hue
```

**Source**
- `GET /api/library/{id}` — single MediaItem.
- Pushes `(bd, hue)` to global backdrop state on mount.

**Side effects**
- `BACK` → return to Catalog.
- `EDIT METADATA` (top-right + in Synopsis row) → opens the drawer (3.2.4).
- `Play first episode` → spawns external player or external link (the app's MVP, per spec, has no built-in player — so this likely opens the file in the OS's default video app).
- `Re-identify` → `POST /api/library/{id}/reidentify` — re-runs the LLM + sources.
- `TMDB` / `MAL` / `IMDb` source buttons → `window.open` to that source's URL.

#### Block: Body (3-column on desktop, stacked on mobile)

**Inputs**
```
item.overview, item.tags[], item.studio, item.lang, item.runtime,
item.episodes, item.id, item.type, item.title, item.conf,
identification: {
  tmdb: { id, confidence },
  mal:  { id, confidence },
  imdb: { id, confidence },
  // anidb optional
}
onDiskPath: string  // computed: /Pool-XX/Media/{type}/{title}
```

**Source**
- Identification data comes back inside `GET /api/library/{id}` — include it in the response payload, no need for a separate call.
- `onDiskPath` is computed server-side from `MediaItem.folder` (the FolderWatcher this title belongs to) + `SectionFolder.path` + the title's directory name.

**Side effects**
- None directly — informational. Re-identify is on the hero.

#### Block: Episodes grid

**Inputs**
```
seasons: { number: int, count: int }[]
episodes: {
  n: int,
  title: string,
  have: bool,                // file exists on disk?
  runtime: string,
  resolution: "1080p" | "2160p" | …,
  codec: "H.264" | "H.265" | …,
  size: string,              // "1.2 GB"
  filePath?: string          // full path if have
}[]
```

**Source**
- `GET /api/library/{id}/episodes?season=N`.
- The `have` flag is the **single most important field** on this page — it drives the entire status visualisation (opacity, border style, thumbnail filter, hover behaviour, status icon, meta line wording). Compute it as `File.Exists(filePath)` server-side.

**Side effects**
- Click episode (if `have`) → play.
- Hover state toggles the play / "EMPTY" overlay (purely client-side).
- Season switcher → re-fetch episodes for the chosen season.

#### Block: Edit Metadata drawer (six tabs)

This drawer is the **manual-correction workhorse**. Most user-driven writes happen here.

**Inputs**
```
item: MediaItem
posterCandidates: {
  id, source, url           // gallery from TMDB/MAL/IMDb + uploaded
}[]
backdropCandidates: {
  id, source, url
}[]
tagPool: string[]           // suggested tags
```

**Source**
- `GET /api/library/{id}/candidates/posters`
- `GET /api/library/{id}/candidates/backdrops`
- `GET /api/tags/suggested`

**Side effects**
| Tab            | Action                            | API                                                        |
|----------------|-----------------------------------|------------------------------------------------------------|
| Source IDs     | Set TMDB / MAL / IMDb / AniDB ID  | `PATCH /api/library/{id}` body `{ tmdbId, malId, imdbId, anidbId }` |
|                | Paste URL → parse ID              | Client parses; falls back to `POST /api/library/{id}/parse-url` |
| Basics         | Edit title / cjk / year / type …  | `PATCH /api/library/{id}` whole-object merge               |
| Poster         | Pick candidate                    | `PATCH /api/library/{id} { posterUrl }`                    |
|                | Upload                            | `POST /api/library/{id}/poster` multipart                  |
| Backdrop       | Pick candidate                    | `PATCH /api/library/{id} { bd }`                           |
|                | Upload                            | `POST /api/library/{id}/backdrop` multipart                |
| Tags           | Add / remove                      | `PATCH /api/library/{id} { tags: [...] }`                  |
| Manage         | Rescan                            | `POST /api/library/{id}/scan`                              |
|                | Identify queue (LLM)              | `POST /api/library/{id}/identify`                          |
|                | Apply renames                     | `POST /api/library/{id}/apply-renames`                     |
|                | Preview renames                   | `GET  /api/library/{id}/preview-renames`                   |
|                | Revert all                        | `POST /api/library/{id}/revert-all`                        |
|                | Delete (metadata only)            | `DELETE /api/library/{id}`                                 |
|                | Delete + files from disk          | `DELETE /api/library/{id}?withFiles=true`                  |
| Re-run LLM     | Footer button                     | `POST /api/library/{id}/reidentify`                        |
| Save changes   | Footer button                     | Commits whichever tab's pending edits via PATCH            |

### 3.3 Torrents

#### Block: Header strip

**Inputs**
```
activeCount: int
totalDown: float MB/s
totalUp: float MB/s
```

**Source**
- SignalR stream `TorrentHub.Counters` — pushes the three numbers at ~1 Hz.

#### Block: Tab segment + table / cards

**Inputs**
```
torrents: TorrentRecord[]
   ↳ each: id, name, dest (identified title string or "— Needs identification —"),
     progress (0..1), dn (MB/s), up (MB/s), peers (string "L/S"),
     eta (string), state, size (string)
```

**Source**
- Initial: `GET /api/torrents`.
- Live updates: `TorrentHub.TorrentUpdated` SignalR events deliver deltas.
- `dest` is **already resolved on the server** to the identified MediaItem's display title — never raw folder names.

**Side effects**
- Add button → opens the AddTorrent drawer/sheet (3.3.2).
- Per-row controls (pause, resume, remove) — not currently in the design but the data model supports them.

#### Block: Add torrent drawer / sheet

**Inputs**
```
mode: "file" | "magnet"
uploadedFile?: TorrentFile { name, sizeBytes, files[] }
magnetUri?: string
destinations: MediaItem[]   // first 12 identified titles, alphabetical
config: { paused, noSeed, flatten, stripRoot, autoRename, dnCap, upCap }
```

**Source**
- Destinations: `GET /api/library?identifiedOnly=true&take=200` (auto-complete) or just take the same payload Catalog uses.
- Parse uploaded torrent: `POST /api/torrents/parse` multipart returns `{ name, files[], suggestedDest? }`.

**Side effects**
- `Add torrent` → `POST /api/torrents` with `{ source, magnet?, fileId?, destination, config }`. Returns the new `TorrentRecord`; UI optimistically appends it.

### 3.4 Settings

Settings is mostly forms; each tab persists to `AppConfig` (key-value store). All changes apply live — no restart.

#### Tab: General

**Inputs**
```
config.language: "en" | "ru"
appsettings (read-only): {
  watcherDelayMs: int,
  videoExtensions: string[],
  subtitleExtensions: string[],
  imageExtensions: string[]
}
```

**Source**
- `GET /api/config/language`
- `GET /api/system/appsettings` (read-only — these come from appsettings.json / Docker env, not the DB)

**Side effects**
- Language change → `PATCH /api/config { language: "ru" }` and refresh i18n bundle without reload.

#### Tab: Root folders

**Inputs**
```
folders: SectionFolder[]
   ↳ each: id, title, path, watchers, identified, missing, hue, bd
```

**Source**
- `GET /api/folders` — same call Catalog uses for its filter chips. Cache.

**Side effects**
- `Rescan` on row → `POST /api/folders/{id}/scan`
- Edit → opens an inline editor / dialog to change title, path, type
- Remove → `DELETE /api/folders/{id}` (with confirmation; files on disk preserved)
- `Add section folder` → opens a "Pick a folder" dialog that lists `/mnt`, `/Pool-D1/Media`, etc., then `POST /api/folders { title, path, type }`

#### Tab: Rename history

**Inputs**
```
history: RenameHistoryEntry[]
   ↳ each: id, at (HH:MM:SS), date (YYYY-MM-DD), file, to, pattern, folder, reverted: bool
stats: { total, today, reverted, patternsUsed }
```

**Source**
- `GET /api/history?from=…&to=…&q=…`
- `GET /api/history/stats`

**Side effects**
- Per-row `Revert` → `POST /api/history/{id}/revert`. Restores the original filename, flips `reverted` flag, broadcasts `HistoryChanged`.

#### Tab: Appearance

**Inputs**
```
config: {
  theme: "system" | "light" | "dark",
  accent: "crimson" | "amber" | "green" | "blue" | "violet",
  backdrop: { enabled: bool, intervalSec: int, blur: int, brightness: int }
}
```

**Source**
- `GET /api/config/appearance`

**Side effects**
- Every slider / toggle / swatch → `PATCH /api/config/appearance`. Apply immediately to CSS vars; persist async.

#### Tab: AI / LLM

**Inputs**
```
llm: {
  provider: "ollama" | "openai" | "groq" | "lmstudio" | "openai-compatible",
  model: string,
  baseUrl: string,
  apiKey: string,         // sent masked; PATCH only when changed
  status: "online" | "offline" | "error",
  queue: { processed: int, total: int },
  avgLatencyMs: int,
  hitRate: float          // % of items where TMDB matched after normalization
}
```

**Source**
- `GET /api/llm/config`
- `GET /api/llm/status` (refresh on focus + every 10s)
- `POST /api/llm/test` for the `Test connection` button

**Side effects**
- `Save & reload` → `PATCH /api/llm/config`; server warm-restarts the LLM client, no app restart.

#### Tab: Patterns

**Inputs**
```
patterns: RenamePattern[]
   ↳ each: id, name, regex, scope ("Global" | "Folder" | "Exclusion"), priority, enabled
```

**Source**
- `GET /api/patterns`

**Side effects**
- Toggle enabled → `PATCH /api/patterns/{id} { enabled }`
- Edit → modal with regex tester, then `PATCH`
- New → `POST /api/patterns`
- Delete → `DELETE /api/patterns/{id}` (with confirmation)

#### Tab: Ignore rules

**Inputs**
```
ignores: IgnoreRule[]
   ↳ each: id, glob, scope, on
```

**Source**
- `GET /api/ignores`

**Side effects**
- Standard CRUD on `/api/ignores`.

#### Tab: Torrent

**Inputs**
```
torrentConfig: {
  listenPort: int,
  dhtPort: int,
  globalDnLimitMbps: int,    // 0 = unlimited
  globalUpLimitMbps: int,
  maxActiveDownloads: int,
  maxActiveSeeds: int,
  encryption: bool,
  dht: bool,
  pex: bool,
  upnp: bool
}
```

**Source**
- `GET /api/torrent/config`

**Side effects**
- `PATCH /api/torrent/config` — applied live; the BT engine reloads its limits.

#### Tab: Metadata

**Inputs**
```
sources: {
  name: "TMDB" | "MAL" | "IMDb" | "AniDB",
  enabled: bool,
  status: string,        // "api-key set", "scraping mode", "off"
  avgConfidence: float   // 0..1, computed over recent identifications
}[]
```

**Source**
- `GET /api/metadata/sources`

**Side effects**
- Toggle / edit → standard PATCH.

---

## 4. Cross-cutting: backdrop state

The global blurred fanart layer is **its own data path** because every page interacts with it:

```
Catalog hero rotates       → setBdImage(featured[i].bd, hue)
MediaDetail mounts         → setBdImage(item.bd, hue)
Settings/Torrents          → leave previous value alone
```

The value lives in app shell state (no API). When the user reloads the page, the backdrop falls back to `LIBRARY[0].bd` (or whichever first item the API returns). No need to persist between sessions.

The backdrop `<div>` layer is `position: fixed; z-index: 0` and cross-fades by stacking up to 3 image layers and swapping their opacity. **Prune to last 2 layers** — see `components.jsx::Backdrop` for the canonical implementation.

---

## 5. SignalR / live data summary

The spec calls for SignalR-driven live UI in Torrents and Explorer. With Explorer gone, the live channels collapse to:

| Hub channel              | Pushed event                                       | UI reaction                                                |
|--------------------------|----------------------------------------------------|------------------------------------------------------------|
| `TorrentHub.Counters`    | `{ activeCount, totalDown, totalUp }`              | Update header strip in /torrents                           |
| `TorrentHub.TorrentUpdated` | `{ id, progress, dn, up, peers, eta, state }`  | Update one row in the torrent table / card list           |
| `TorrentHub.TorrentAdded`   | `TorrentRecord`                                 | Prepend to the list                                        |
| `TorrentHub.TorrentRemoved` | `{ id }`                                        | Remove from the list                                       |
| `IdentificationHub.QueueChanged` | `{ processed, total }`                      | Update the LLM hero card on Settings → AI/LLM and the sidebar footer |
| `IdentificationHub.ItemIdentified` | `MediaItem`                               | Optionally refresh that item's grid card                   |
| `IdentificationHub.NeedsReviewChanged` | `{ count }`                           | Update the NR chip count in Catalog filter bar             |
| `HistoryHub.EntryAdded`   | `RenameHistoryEntry`                              | Prepend to Settings → Rename history (if open)             |
| `HistoryHub.EntryReverted`| `{ id }`                                          | Flip the row's `reverted` flag                             |

---

## 6. What's *not* in this design

- No watch / progress / "Continue watching" data path — explicitly out of scope per the spec.
- No multi-user — no User entity, no auth, no avatars, no roles, no sharing UI.
- No built-in player — `Play` buttons open the OS default app.
- No recommendations / discovery — the catalog is what you have on disk, period.

If any of these become in-scope later, the design surfaces that would change are flagged in `README.md`.
