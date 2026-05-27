# Handoff: Animarr — v3 Desktop (Full-Width) + Mobile

## Overview

**Animarr** is a self-hosted media library manager (anime / donghua / movies / series) with a built-in BitTorrent client and an LLM-driven identification pipeline. Single-user, no auth, runs in Docker. The defining characteristic is that **LLM is the primary identification engine** — it normalizes messy folder names into canonical titles before TMDB/MAL/IMDb are queried, and metadata is stored in SQLite so the file tree on disk is never reorganized.

This handoff bundles **two design surfaces**:
1. **Desktop "v3" (full-width)** — wide horizontal layout that uses the entire viewport
2. **Mobile** — iPhone-sized vertical layout with bottom tab navigation

Both surfaces share a visual language, data model, and component vocabulary.

## About the Design Files

The HTML/JSX files in this bundle are **design references**, not production code. They are React + Babel prototypes that render the intended look, behavior and interaction patterns. They run standalone (open in a browser) so you can pixel-inspect anything you need.

The task is to **recreate these designs in the target codebase's existing environment** — the project description calls for **Blazor Server (.NET 10) + Microsoft Fluent UI Blazor** with **WinUI 3** as the secondary desktop client. Use those frameworks' established patterns. The HTML is for visual reference only — do not ship it.

For the mobile surface specifically: there is no existing iOS/Android client. The closest framework match is **.NET MAUI** or a responsive PWA from the Blazor Server app. The mobile design uses iOS Human Interface Guidelines conventions (bottom tab bar, bottom sheets, scroll-driven hero) — they apply to Android too with minor adjustment.

## Fidelity

**High-fidelity.** Final colors, typography, spacing, transitions, and component states are all locked in. Recreate pixel-perfect using the codebase's component library. Where Fluent UI Blazor primitives exist (forms, dialogs, tabs, toggles), prefer them over hand-rolled equivalents — but keep the visual treatment from the prototype (dark theme, accent color, type hierarchy).

## Design Tokens

Every screen reads from the same CSS custom property layer. Wire these into your theming system:

### Colors

```
--bg-0:        #0a0807    /* page background, deepest */
--bg-1:        #100d0b    /* one step above bg-0, used inside panels */
--surface:     #15110e    /* default card / panel background */
--surface-2:   #1c1814    /* elevated panel, drawers */
--surface-3:   #25201b    /* hover / active tile */
--border:      rgba(255, 240, 220, 0.07)   /* default 1px line */
--border-strong: rgba(255, 240, 220, 0.14) /* emphasised line */
--text:        #f4eee6
--text-dim:    #a8a097
--text-faint:  #5d564f

--accent:      oklch(0.66 0.20 25)         /* crimson — primary brand */
--accent-hi:   oklch(0.74 0.21 25)         /* hover / brighter variant */
--accent-soft: oklch(0.66 0.20 25 / 0.16)  /* tinted background */
--accent-line: oklch(0.66 0.20 25 / 0.40)  /* border on accent surfaces */

--success:     oklch(0.74 0.15 150)        /* "on disk", "downloading OK" */
--warn:        oklch(0.80 0.17 75)         /* "missing", "needs review" */
--info:        oklch(0.74 0.10 240)
```

Accent is **swappable at runtime** — Tweaks panel exposes 5 presets (crimson default + amber, green, blue, violet). Build the theme system to accept any oklch hue and recompute these four accent variables.

### Typography

```
--font-ui:      'Geist', system-ui, sans-serif       /* body, controls */
--font-display: 'Archivo Black', 'Geist', sans-serif /* hero titles, page H1s */
--font-mono:    'Geist Mono', ui-monospace, monospace /* IDs, paths, file lists, meta strips */
--font-cjk:     'Noto Serif SC', serif               /* decorative CJK watermark */
```

Type scale:
- **Display XL** — 96–120 px / line-height 0.86–0.88 / letter-spacing −2 to −3.4. Catalog hero, MediaDetail H1.
- **Display L** — 44 px / 0.95 / −1. "SEASON 01" headers.
- **Display M** — 22 px / 1.1 / −0.4. Card group titles, drawer header.
- **UI body** — 13–15 px / 1.55–1.65. Synopsis, descriptions.
- **UI meta** — 11–12.5 px monospace, letter-spacing 0.6–1.4, uppercase. Status, mata strip, table headers.

### Spacing

8-point ish, but in practice: **4 / 6 / 8 / 10 / 12 / 14 / 16 / 18 / 22 / 26 / 36 / 48** px. Border radius scale: 4 (badges) / 6–8 (controls, chips) / 10–12 (cards) / 14 (panels) / 18 (hero cards) / 22 (sheet handles, modals).

### Shadows / motion

- Cards: `0 10px 30px -16px rgba(0,0,0,0.7), inset 0 0 0 1px rgba(255,255,255,0.04)`
- Modal: `0 40px 80px -20px rgba(0,0,0,0.7)`
- Drawer: `-30px 0 60px -20px rgba(0,0,0,0.6)`
- Backdrop blur: `blur(14px) brightness(38%) saturate(0.95)` on `--bg-0`
- Most transitions: 200 ms ease. Backdrop cross-fade: 1.4–1.8 s ease.
- Hero CJK watermark fade-in: 1.4 s `translateY(-12px) → 0`.

## Visual System

- **Always-on backdrop slideshow.** Every page sits on top of a fixed-position blurred fanart slideshow sourced from the user's library. It cross-fades on title change. Hero blocks paint the same fanart sharp/full-brightness while the global backdrop continues blurred behind everything else. This is a defining product trait — do not skip it.
- **CJK watermark.** Hero blocks include a giant decorative Chinese/Japanese title in vertical-rl orientation at low opacity. Sets the "this is an Asian media library" tone without screaming it. Match exactly in v3 and mobile: `font-size: 280–360 px`, `color: oklch(0.95 0.1 <hue> / 0.07–0.09)`, `writing-mode: vertical-rl; text-orientation: upright`.
- **Film-grain overlay.** A SVG noise pattern at `opacity: 0.06; mix-blend-mode: overlay` over the whole viewport. Adds texture without obscuring anything.
- **Status edges.** Episode cards and torrent rows convey state via a coloured left strip (3 px) + a small chip in the corner (✓ for on disk, ⚠ for missing/needs review). Avoid text-only badges where an icon will do.

## Domain Model (already in the spec, restated for clarity)

```
MediaItem
  id, title, cjk, englishTitle, year, type (Anime|Movie|Series|Multserials),
  hue (degrees for tint), bd (backdrop URL), conf (0..1 LLM/match confidence),
  episodes, season, rating, runtime, studio, lang, overview, tags[]

FolderWatcher / SectionFolder
  id, title, path, watchers, identified, missing, hue, bd

TorrentRecord  state ∈ downloading|seeding|queued, peers, dn/up MB/s, eta, size, dest, progress
RenamePattern  regex (named groups: title, season, episode, year), priority, scope ∈ Global|Folder|Exclusion
IgnoreRule     glob, scope, on
RenameHistory  at, date, file, to, pattern, folder, reverted

AppConfig (key-value)
  LLM provider/baseUrl/model/apiKey, backdrop on/blur/brightness/rotateSec,
  theme accent, language, torrent ports + Mbps caps, video extensions, etc.
```

## Architecture (high-level)

Both surfaces have the same five top-level destinations, now with **Library and Rename History merged into Catalog and Settings respectively**:

| Surface  | Nav             | Destinations                                                              |
|----------|-----------------|---------------------------------------------------------------------------|
| Desktop  | Left sidebar    | Catalog · Torrents · Settings                                             |
| Mobile   | Bottom tab bar  | Catalog · Torrents · Settings                                             |

Settings on both surfaces contains nested config: **General · Root folders · Rename history · Appearance · AI / LLM · Patterns · Ignore rules · Torrent · Metadata**. Mobile renders each as a sub-screen with `chev-l` back; desktop uses a vertical tab list.

When the user clicks a poster in Catalog → MediaDetail opens (in-place navigation, not a route, just state). Mobile hides the tab bar in detail view.

## Screens / Views

### 1. Catalog

The library landing page. Two halves:

**Hero (top, ~70 vh on desktop, ~460 px on mobile)**

- Full-bleed fanart from a 5-item "featured" list (rating ≥ 8.0), auto-rotates every `tweaks.rotateSec` seconds, dot pager on mobile / numbered pager on desktop.
- Layered gradients for legibility: side dark wash on left, bottom dark wash, accent radial at bottom-left tinted by current hue.
- CJK watermark in vertical-rl at top-right, very low opacity.
- Bottom-left content: type pills + tags pills → display title (UPPERCASE, 110–120 px desktop, 38 px mobile) → mono meta strip (★ rating, year, episode count, studio, language) → `<p>` overview → primary action "Open detail" + source buttons.
- Hero rotation also pushes the current image into the global backdrop state so the page-wide blurred backdrop matches the hero.

**Filter bar (below hero)**

A single row of controls. Layout left to right:
1. Tab segmented control: All · Anime · Movie · Series · Multserials. 36 px tall, padding 4 px outer + 7×14 inner button. Active pill = `--surface-3` background + 1 px inset `--accent` shadow.
2. Search input (max 360 px wide), 36 px tall, with search icon and an x to clear.
3. **`Rescan all`** ghost button.
4. **NeedsReview chip** — only renders when `NEEDS_REVIEW.length > 0`. Warn-tinted background, count badge, pulsing dot. Click opens a modal (desktop) or bottom sheet (mobile) listing folders with candidate matches.
5. Count `{filtered}/{total} TITLES` right-aligned mono.

Below the row: a second, lighter chip strip for **filter by folder** — "FOLDER · All · Anime · Movies · Multserials · Serials · Donghua". Active chip = solid accent fill. Folder filter is independent of the type filter (AND-combined).

**Grid**

`display: grid; grid-template-columns: repeat(auto-fill, minmax(180–220px, 1fr)); gap: 18–22px`. Desktop v3 uses min 220 px (≈ 10 columns on a 1920 wide screen). Mobile uses two fixed columns.

Each poster is a `<button>` with:
- Procedural background from `bd` URL with `saturate(0.85) brightness(0.85)` + a color-mode wash tinted by the title's hue → unifies disparate source artwork into the brand.
- Top-right CJK watermark in vertical-rl.
- Top-left small mono pill — type ribbon ("ANIME", "MOVIE"…).
- Bottom-right amber confidence badge when `conf < 0.85`.
- Bottom block on a dark gradient: display title (UPPERCASE display font, 14–17 px) + mono meta line "year · {n} EP · ★ rating".
- Hover: lifts 3 px, accent-tinted shadow + border.

### 2. MediaDetail

Reached by clicking a poster. Pushes the item's backdrop to the global state on mount.

**Desktop v3 (full-width)**

- Hero: 68 vh, min 580 px. Same fanart treatment as Catalog hero but with `BACK` button (top-left, glass-blur chip) and `EDIT METADATA` button (top-right, same chip style). Bottom row is `260 px poster | flex 1 title block`. Poster has no `translateY` — its bottom aligns with the hero's bottom 90 px. Title block: pill row → 96 px UPPERCASE H1 → meta strip (★ year · studio · runtime · episodes · language · NN% MATCH).
- Below hero, 3-column body fills the full width:
  - **Synopsis** (2.2 fr) — overline "SYNOPSIS" mono accent + body paragraph + buttons row ("Play first episode" primary, "Edit metadata" solid, "Re-identify" ghost).
  - **Details** (1 fr) — overline "DETAILS" + mono key/value list (Studio, Language, Runtime, Episodes, Tags) + source buttons row (TMDB / MAL / IMDb).
  - **Identification** (280–360 px fixed) — surface card with TMDB/MAL/IMDb IDs in mono rows with confidence colour, ON DISK path at the bottom.
- Episodes section: same column padding as page, full-width grid `repeat(auto-fill, minmax(260px, 1fr))`. Season switcher chips top-right of the section header.

**Mobile**

- Hero: 540 px. BACK and EDIT chips in the corners. Bottom row: 110 px poster + title block. H1 is 26 px UPPERCASE.
- Below hero stacked: Primary action button row (`Play first episode` full-width + `Re-identify` square icon button). Mono meta strip wrapping. Synopsis. Horizontal-scroll source chips (TMDB / MAL / IMDb with confidence). Episode list **as rows** (not cards): coloured left strip + small thumbnail + title/meta + check/warn icon on the right. Compact, dense, fingertip-friendly.

### 3. Episode card / row (status visualisation)

This is shared visual language — apply it everywhere episodes appear.

```
ON DISK (have file)                     MISSING (no file)
─────────────────                       ───────────────
opacity: 1                              opacity: 0.45–0.50
border: 1px solid --border              border: 1px dashed rgba(255,240,220,0.12)
thumbnail: full color                   thumbnail: grayscale + brightness 0.55
left strip: 3px solid --success         left strip: 3px solid --warn
                +0 0 12px glow
corner icon chip: ✓                     corner icon chip: ⚠
hover overlay: ▶ play button            hover overlay: dashed box ⬇ "EMPTY"
meta line: 1080p · H.265 · 0.84 GB      meta line: "Not downloaded" (warn color)
title color: --text                     title color: --text-dim
```

The "empty" hover is a dashed-bordered 56 px square containing a download glyph and the word EMPTY in mono — it makes "the file isn't here yet" the immediately readable state, not a play affordance you'd accidentally click.

### 4. Torrents

Desktop and mobile share the data model but differ in layout. Desktop is a table; mobile is a card list.

**Both show:**
- Live counter strip: DOWN MB/s (accent), UP MB/s (success), pulsing state dot on each row.
- Tab filter: Active · Queued · All (counts on each tab).
- Per-row: name (mono), destination ("identified title" or warn-coloured "— Needs identification —"), progress bar (gradient = accent for downloading, success for seeding), down/up speed, peers, ETA, size.

**Add torrent**
- Desktop: right-side **drawer** 480 px.
- Mobile: **bottom sheet** at 92 % height.

Both have the same fields: File/Magnet toggle → file drop zone (or magnet input) → file list with per-file priority chips (Normal/High/Low/Skip) → destination dropdown that **lists identified titles, not raw folder names** (this is critical UX — manual labels > auto-identification) → manual path input → ↓/↑ Mbps caps → toggles (Start paused / Stop after download / Flatten subfolders / Strip root / Auto-rename).

### 5. Settings

Vertical-tab layout on desktop (sticky 220 px column), sub-screen drill-down on mobile.

Tabs:
- **General** — language picker + read-only application parameters (Watcher delay, video extensions, subtitle extensions, image extensions).
- **Root folders** — the merged "where Animarr watches" UI. Each row: 36 px folder thumb (tinted by hue), title, monospace path, status pills `{n}w {n} ID {n}?`, `Rescan` ghost button, edit + remove icons. Bottom: `+ Add section folder` primary.
- **Rename history** — moved here from a top-level route. 4 KPI tiles (Total / Today / Reverted / Patterns used) + grouped-by-date timeline. Each row: timestamp · original filename (struck through if reverted) · `→` · new filename · pattern pill · `↶ Revert` flat action.
- **Appearance** — theme (system / light / dark), accent swatches (5 colors), backdrop section with toggle + rotation interval + blur slider 0–30 px + brightness slider 10–80 %.
- **AI / LLM** — hero status card (accent gradient, ✨ icon, "ACTIVE · ollama · qwen2.5:1.5b", queue progress 17/25, hit-rate metrics) + provider dropdown (Ollama / OpenAI / Groq / LM Studio / OpenAI-compatible) + model + base URL + API key + Save & reload.
- **Patterns** — regex table with name, regex, scope (Global / Folder / Exclusion), priority, on/off toggle, edit/delete.
- **Ignore rules** — glob table, on/off, scope, delete.
- **Torrent** — listen port / DHT port / global Mbps caps / max active downloads / max active seeds / encryption / DHT / PEX / UPnP toggles.
- **Metadata** — TMDB / MAL / IMDb / AniDB rows with on/off toggle, status text, avg confidence colored.

### 6. Edit Metadata Drawer

The user pointed out that there was no way to manually correct a wrong match. This drawer is the answer.

Triggered by `EDIT METADATA` button on MediaDetail (top-right hero chip + button in Synopsis row on desktop / chip on mobile). Right-side drawer 560 px on desktop / bottom sheet 88 % on mobile.

Six tabs:

1. **Source IDs** — green LLM-match banner at top showing current confidence + the normalization the model performed. Below: TMDB, MAL, IMDb, AniDB ID fields with `↗ open source` link and a `Search` button next to each. At the bottom: "Or paste a source URL" field that parses out the ID automatically.
2. **Basics** — Display title / English title / Original (CJK, rendered in `font-cjk`) / Year / Type / Language / Studio / Runtime.
3. **Poster** — grid of 10 candidate posters from the metadata sources, the selected one wrapped in an accent border with a checkmark chip. Mock label below each: "TMDB", "MAL", "IMDb", "Local". `+ Upload custom` at the bottom.
4. **Backdrop** — 2-column grid of 12 backdrop candidates, same selection visual. `+ Upload custom`.
5. **Tags** — add tag input (Enter to commit) → chips with x → "Suggested" row of common tags below.
6. **Manage** — three info blocks each with action buttons:
   - **Rescan files** — `Rescan now` / `Identify queue (LLM)`
   - **Apply pending renames** — `Apply 12 renames` / `Preview`
   - **Revert all renames in this folder** — `Revert all`
   - Below: **Danger zone** (red-tinted card) — `Also delete files from disk` toggle (off by default), then a `Remove from Animarr` button that asks for confirmation before destruction. With toggle ON it changes to `Remove + delete files`. Two-step confirm.

Footer: `Save changes` primary + `Cancel` ghost + `Re-run LLM` flat on the right.

### 7. NeedsReview popup

NR is **not** a separate page — it's a popup triggered from Catalog's filter bar. Modal (720 px) on desktop, bottom sheet on mobile. Body lists each unresolved folder with:
- Folder name in mono
- 2–3 candidate cards side-by-side: small poster + title + year + source + confidence color + `Use` button
- `+ Search manually` empty card

Picking a candidate writes to SQLite and removes the entry. Folders on disk are never touched.

## Interactions & State

- **Backdrop image sync.** A single `bdImage` state lives in the app shell. Catalog updates it when featured rotates; MediaDetail sets it on mount. The global `<Backdrop>` reads from it. Use a 1.4–1.8 s opacity cross-fade between layers — see `mobile-app.jsx::MobileApp` and `app-v3.jsx::AppV3` for the pattern.
- **Live torrents.** `setInterval` 1100 ms tick that advances the progress percentage of any row with `state === "downloading"`. Real implementation: SignalR (`TorrentHub`).
- **Tweaks panel.** A right-anchored floating panel that lets the user adjust accent, density, backdrop on/off + blur + brightness, hero rotate seconds. Persisted to the EDITMODE JSON block at the top of `app-v3.jsx`/`mobile-app.jsx`. In production, persist to `AppConfig`.

## Files in this bundle

- `Animarr v3 — Full Width.html` — desktop entry point
- `Animarr Mobile.html` — mobile entry point (renders inside an iPhone bezel for review)
- `data.jsx` — mock library, torrents, history, folders, patterns, ignore rules. Use this to understand the domain shape.
- `components.jsx` — shared primitives: `Icon`, `Logo`, `Sidebar`, `Backdrop`, `Poster`, `Btn`, `Pill`, `Toggle`, `Field`, `Input`, `Select`, `PageHeader`, `Container`. **Start here when picking what to reuse from Fluent UI Blazor vs. build custom.**
- `tweaks-panel.jsx` — the floating Tweaks UI (likely not shipped to production; useful for design iteration during dev).
- `screens.jsx` — v1 (contained) versions of the screens. v3 reuses the Settings / Torrents implementations from this file via `window.SettingsScreen` etc.
- `screens-v3.jsx` — full-width Catalog, MediaDetail, Edit drawer, episode card.
- `app-v3.jsx` — desktop shell, route state, backdrop sync, Tweaks wiring.
- `mobile-app.jsx` — full mobile app (catalog + detail + torrents + settings + sheets, no external screens-v3 dep).
- `ios-frame.jsx` — iPhone bezel for review only. Drop it on real device.

## Implementation order suggestion

1. **Theming + tokens** — wire CSS variables, font imports, accent-swap mechanism. Confirm dark mode renders correctly with Fluent components inside.
2. **Backdrop + Sidebar shell** — get the cross-page chrome right before touching content.
3. **Catalog hero + grid** — biggest visual statement. Get fanart slideshow, filter row alignment, poster card treatment locked in.
4. **MediaDetail** — apply hero treatment + body layout. Episode card status visualisation.
5. **EditMetadataDrawer** — six tabs, especially Source IDs + Manage (this is the workhorse panel for the manual-correction workflow).
6. **Settings** — vertical tabs; port Rename history + Root folders here.
7. **Torrents + AddTorrent drawer** — table + drawer pattern.
8. **NeedsReview popup** — last because it depends on Catalog being done.
9. **Mobile** — once desktop is locked, the mobile design is mostly responsive constraints + bottom sheets in place of drawers.

If the codebase doesn't have a comparable component for something (e.g. a "glass" chip), build it from scratch rather than forcing a Fluent primitive — match the prototype.

## Open questions for the implementing developer

- The backdrop slideshow is on a `position: fixed` layer at z-index 0. Make sure Blazor's render mode and Fluent components don't fight this. Test long-running pages for memory pressure on the cross-fade `<div>` layer growth — prune to last 2.
- Tweaks panel — ship or not? The design uses it for live theme/backdrop tweaking. If shipped, it likely lives in the same Settings → Appearance section with the same sliders.
- Mobile target: PWA via Blazor, MAUI native, or both? The current design works for either. Bottom sheets degrade to centered modals on desktop if you ship a single responsive PWA.
