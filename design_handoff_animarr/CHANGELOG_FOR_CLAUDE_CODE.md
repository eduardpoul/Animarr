# Changelog for Claude Code — recent iterations

These are the changes made to the design prototype since the previous handoff.
Apply them to whatever you've already started building.

---

## 1. Watch tracking is now part of the spec

Previously the spec said "no watch progress". The user has reversed that decision. Every episode and every movie file now carries two new fields:

```
watched: bool          // user marked it done (or auto-set when progress hits 100%)
progress: 0..1         // how much of the runtime the player has reported
```

These persist to SQLite (a new `WatchState { mediaFileId, watched, progressMs, lastSeenAt }` table is the cleanest fit). No new endpoints required — extend the existing episode / file payloads.

---

## 2. New "Continue" primary CTA on MediaDetail

The big primary button under the title is no longer always `Play first episode`. It resolves dynamically:

| Library state                                                           | Button label                                          |
|-------------------------------------------------------------------------|-------------------------------------------------------|
| Nothing watched, nothing in progress, episodes available                | `Play first episode`                                  |
| User abandoned an episode mid-watch                                     | `Continue · EP 05` (the lowest in-progress episode)   |
| Some watched, no in-progress, but more episodes exist                   | `Continue · EP 06` (next-after-last-watched on disk)  |
| Everything watched                                                      | `Watched · Replay`                                    |
| **Movie** — no watch state                                              | `Play movie`                                          |
| **Movie** — in progress                                                 | `Continue · 62%`                                      |
| **Movie** — watched                                                     | `Rewatch from start`                                  |

Resolution logic (server-side or client-side, your call):
1. Look at on-disk episodes ordered by `(season, n)`.
2. First entry where `have && !watched && progress > 0` → "Continue · EP NN".
3. Else: if any episode is watched, next non-watched entry → same label.
4. Else: first `have && !watched` → "Play first episode".
5. Else: "Watched · Replay".

For movies, the single file's state drives the same logic, with movie-specific copy.

---

## 3. Movies now have a file card on MediaDetail

Previously movies showed only the hero and the body — there was no equivalent of the episodes grid because a movie is one file. Now `type === "Movie"` renders a dedicated **FILE · ON DISK** section after the body, sharing the visual language of episode cards:

- **Layout**: 16:9 thumbnail panel on the left (360px wide on desktop), info panel on the right with title, filename, four mono key/value cells (RUNTIME / RES / CODEC / SIZE), and two action buttons (`Continue · NN%` / `Play movie` / `Rewatch from start` + a `Mark as (un)watched` toggle).
- **Left edge strip** (3 px green) on the wrapping card — same as episode cards.
- **Watch progress bar** drawn at the bottom of the thumbnail (4 px, accent gradient when in-progress, grey when watched).
- **Two corner controls on the thumbnail** — see "Status icons" below.
- **Mobile** version: same idea, vertical (thumbnail on top, info below). Action buttons collapse to icon-only when needed.

Reference frames:
- `pages/desktop/21-media-detail-movie.html`
- `pages/mobile/10-media-detail-movie.html`

---

## 4. Status icons split into two corners (final version)

Every video file — episode or movie — now shows **two** small status chips on its thumbnail, in different corners. Treat them as the canonical episode/file UI for the rest of the build.

```
┌─────────────────────────────────────┐
│ 04                          [ ✓ ]   │  ← TOP-RIGHT: disk-status (immutable)
│                                     │     ✓ green border + tint = file on disk
│        (thumbnail / cover)          │     ⚠ amber border + tint = missing
│                                     │
│                             [ 👁 ]   │  ← BOTTOM-RIGHT: watched-toggle (user-controlled)
│ ──── progress bar ──────────────────│     open eye  = unwatched (default)
└─────────────────────────────────────┘     closed eye = watched
  Episode 4
  20m · 1080p · H.265                 .74 GB
```

### Sizing — must match across desktop and mobile

| Surface                       | Box size      | Radius |
|-------------------------------|---------------|--------|
| Desktop episode card          | 26 × 26 px    | 6 px   |
| Desktop movie file card       | 30 × 30 px    | 8 px   |
| Mobile episode row            | 24 × 24 px    | 6 px   |
| Mobile movie file row         | 30 × 30 px    | 8 px   |

**Disk-status and watched-eye must be the same size on the same card.** They are conceptually peers — one is system state, one is user state. Don't make one bigger than the other.

### Watched state visuals (when toggled on)

- Card opacity drops to 0.72 (0.7 on mobile)
- Thumbnail gets `filter: saturate(0.6) brightness(0.7)`
- Left edge strip changes from green-with-glow to flat `--text-faint`
- Eye icon flips to "open" with green tint
- Title color drops from `--text` to `--text-dim`

### Progress bar

3 px tall (4 px on movie file card), pinned to the bottom edge of the thumbnail. Background `rgba(255,255,255,0.08)`. Fill is `linear-gradient(90deg, --accent, --accent-hi)` with `0 0 8px var(--accent-soft)` glow when in progress; flat `--text-faint` when watched (100%).

### Meta text changes — IMPORTANT

We deliberately **removed all status words from the meta line**. Do not bring them back. The meta line is now strictly:

```
ON DISK:   {runtime} · 1080p · H.265 · {size}
MISSING:   {runtime} · Missing
```

No "Watched", no "38% watched", no "Not downloaded". The visual treatment (opacity, strip color, eye icon, progress bar, disk-status icon) communicates the state. Text would be redundant.

### Hover behaviour (unchanged)

- On disk → centered play button overlay
- Missing → centered dashed-bordered box with download icon + `EMPTY`
- The watched-eye stays visible always; clicking it doesn't trigger the play action (stopPropagation).

---

## 5. NeedsReview shifted into Catalog filter row

The previous design had `Library / Explorer` as a separate destination. That's gone — Library merged into Catalog and Settings. As part of that, the NeedsReview banner is now a **chip in the Catalog filter row**:

- `NEEDS REVIEW · N` chip — only renders when count > 0
- Warn-amber background, pulsing dot indicator
- Click opens a modal (desktop) or bottom sheet (mobile) listing the unresolved folders with candidate matches and a `Use` button each

Old position (top of Library page banner) — remove if you still have it.

---

## 6. Rename history moved to Settings

Previously a top-level destination. Now a tab inside Settings (`Settings → Rename history`). Same content, same data — just no longer in the sidebar / tab bar.

The sidebar / tab bar is now just 3 entries on both desktop and mobile:

```
Desktop: Catalog · Torrents · Settings
Mobile:  Catalog · Torrents · Settings
```

---

## 7. Edit Metadata drawer gained a Manage tab

Six tabs total: `Source IDs · Basics · Poster · Backdrop · Tags · Manage`.

The new **Manage** tab consolidates everything that used to live on the Library page's per-section actions:

- `Rescan now` / `Identify queue (LLM)`
- `Apply pending renames` (with `Preview` button) / `Revert all renames in this folder`
- **Danger zone** card (red-tinted) with:
  - `Also delete files from disk` toggle (off by default)
  - Two-step confirm button: `Remove from Animarr` → `Yes, remove from library` (or with the toggle on: `Remove + delete files` → `Yes, delete everything`)

Reference: `pages/desktop/09-media-detail-edit-manage.html`

---

## Files to re-read

The shape of the underlying React prototypes hasn't changed structurally — only the components below were modified. Cross-reference these for the exact final HTML/CSS:

| File                  | What changed                                              |
|-----------------------|-----------------------------------------------------------|
| `screens-v3.jsx`      | `EpisodeCardV3` rewrite, new `MovieFileCardV3`, `EyeIconV3`, `computeContinue` logic, `Manage` drawer tab |
| `mobile-app.jsx`      | `EpisodeRowM` rewrite, new `MovieFileRowM`, `EyeIconM`, continue action, NR sheet, Settings sub-screens |
| `screens.jsx`         | `SettingsScreen` adds `folders` and `history` tabs; v1 episode card mirrors v3's missing-state visuals; `NRBadge` + `NRModal` + folder chip row in Catalog |
| `CANVAS.html`         | Updated episode card preview tile to show all 4 states; added `21 — Movie` and `M10 — Movie` to the page index |
| `pages/desktop/21-…`  | New — Movie detail state |
| `pages/mobile/10-…`   | New — Mobile movie detail state |

---

## Quick visual check

Open `design_handoff_animarr/CANVAS.html` and scroll to:
- **Section 01.6** — "Episode cards — 4 states" tile shows the canonical have / watched / in-progress / missing visuals side by side.
- **Section 03.2** — Media detail frames include the new MOVIE state (#21).
- **Section 04** — Mobile frames include M10 movie variant.
