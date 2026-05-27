# Changelog #2 for Claude Code — Continue Watching + Favorites + Downloads

These changes go on top of the previous changelog. Pull these in next.

---

## 1. New "Continue Watching" hero on Catalog

The Catalog hero is no longer a pure Featured rotation. It is now a **5-slot hero** with this fill order:

1. Items the user has actively watched (mid-watch, `progress > 1 minute && !watched`).
2. "Next up" entries — series where the user finished an episode and a later one is on disk.
3. If <5 after the above, fill with **Featured** items (rating ≥ 8.0).
4. If still <5, fill with random library entries.

The hero always shows exactly 5 slots. Rotation interval comes from `tweaks.rotateSec`.

### Slot rendering
Each slot has a `slotKind` field that drives the overline / copy:

- `slotKind === "cw"` with `cwKind === "progress"` →
  - Overline: `CONTINUE · EPISODE 05`
  - Mid-paragraph: progress bar with `~Xm remaining / NN%`
  - Primary CTA: `Continue · 38%`
  - Secondary CTA: `Restart episode`
- `slotKind === "cw"` with `cwKind === "next"` →
  - Overline: `NEXT UP · EPISODE 09`
  - Primary CTA: `Play episode 09`
- `slotKind === "featured"` → Overline: `FEATURED`, CTA: `Open detail`
- `slotKind === "random"`  → Overline: `FROM YOUR LIBRARY`, CTA: `Open detail`

### Pager
Right column of the hero, one row per slot:

```
─── 01 | 38%        ← in-progress
─── 02 | 72%
─── 03 | Next up    ← new episode ready
─── 04 | ★          ← featured
─── 05 | Lib        ← random library
```

### Below the hero
A horizontal row labelled `EDITOR'S PICKS · FEATURED · ★ 8.0+` listing the featured items that **didn't already appear** in the hero slots (deduplicated by id). 184×268 posters, horizontal scroll, no scrollbar.

### Domain shape

```
WatchState {
  mediaItemId,
  episode?  : int,
  progress  : 0..1,
  kind      : "progress" | "next"     // "next" = unfinished follow-up episode on disk
  lastSeen  : Date
}
```

Backend endpoint suggestion: `GET /api/library/continue?take=5` returns the slot-ranked list (server resolves the priority described above so the client just renders).

---

## 2. Tags / Folders filter switcher

The chip strip below the toolbar is no longer hardcoded folders. There is now a 2-way toggle:

- **Tags** (default) — chips derived dynamically from `LIBRARY[*].tags`, ranked by frequency. "All" first, then top tags.
- **Folders** — chips from `FOLDERS[*].title`, same UI.

UI:

```
[ search ]  [ Tags | Folders ]  [ Rescan ]  [ NR · 2 ]              24 / 24

TAG  All  Donghua  Cultivation  Sci-fi  Action  Animation  Adult  Mecha …
```

Switching modes resets the chip selection to "All". Items are filtered by whichever mode is active (not both at once). Search + chip + grid AND-combine.

---

## 3. Favorite (star) button on MediaDetail

A new control sits in the top-right cluster of the MediaDetail hero, **left of** `EDIT METADATA`:

- Default: filled-dark glass chip with empty star outline + label `FAVORITE`
- Active: amber-tinted background, filled star, label `FAVORITED`
- Click toggles `window.FAVORITES.add/delete(id)` in the prototype; in production patch `MediaItem.favorited`.

Reuse the same glass styling as the existing BACK / EDIT chips so the three line up.

Suggested mobile equivalent (not yet built): a heart/star icon at the same corner of the mobile hero, same toggle behavior.

---

## 4. Torrents → Downloads (UI label only)

The destination ID stays `torrents` (routes, hub names, SQLite column — all unchanged). The user-facing label is now **Downloads** everywhere:

- Sidebar nav: `Torrents` → `Downloads`
- Mobile tab bar: `Torrents` → `Downloads`
- Page title: `TORRENTS` → `DOWNLOADS`
- Subtitle: now mentions "Torrents, magnet links and direct file uploads."
- "Add torrent" button → "Add download"
- Drawer header: `ADD TORRENT` → `ADD DOWNLOAD`
- Drawer primary CTA: `Add torrent` → `Start download`

---

## 5. AddDownload drawer — new "Upload files" source

The source selector is now a **3-way segment**:

```
[  .torrent file  |  Magnet link  |  Upload files  ]
```

The first two are unchanged. The new **Upload files** mode shows a `FilesUploadZone`:

- Large dashed-bordered drop area (`200×ish` px tall) with cloud-upload icon, "Drop files here" headline, "or browse to select multiple" sub-line, and an "MP4 · MKV · AVI · ZIP · ANY — no size limit" hint
- Drag-over state: dashed border switches to accent color, background fades to `--accent-soft`
- Selected files list below: filename (mono, ellipsis), size, `x` to remove. Header line shows `FILES (3) · 3.0 GB est.`
- The downstream pipeline treats them as if they came from a torrent: same destination dropdown ("identified titles"), same auto-rename toggle, same per-file priority chips, same progress rendering in the Downloads list.

Backend impact:
- New `POST /api/downloads/upload` multipart endpoint that accepts N files + destination + per-file priority array
- The resulting record reuses `TorrentRecord` shape with `source: "upload" | "torrent" | "magnet"` so the Downloads list rendering doesn't need a separate code path
- SignalR `TorrentHub.*` events fire identically

---

## 6. Demo data added

`data.jsx` exports two new globals used by the new UI:

```js
WATCHING:  Array<{ id, ep?, progress, kind: "progress"|"next" }>
FAVORITES: Set<id>
```

Replace these with real backend reads. `WATCHING` is the basis for Catalog hero's CW slots; `FAVORITES` drives the star button state.

---

## Files touched

- `screens-v3.jsx` — `CatalogV3` hero rewrite (5-slot, fill-from-featured), `ChipV3`, `SearchInputV`, `FavoriteButtonV3`, Featured row, dynamic tag/folder switcher; `MediaDetailV3` hero top-right cluster
- `screens.jsx` — `AddTorrentDrawer` 3-tab segment with `FilesUploadZone`, page header rename, button labels
- `components.jsx` — Sidebar label "Torrents" → "Downloads"; new `star` / `star-fill` icons
- `mobile-app.jsx` — Tab bar label rename
- `data.jsx` — `WATCHING`, `FAVORITES` exports

Reference pages (re-render after pulling):
- `pages/desktop/01-catalog.html` — new hero
- `pages/desktop/03-media-detail.html` — favorite star
- `pages/desktop/11-torrents-add.html` — 3rd source tab + file zone
