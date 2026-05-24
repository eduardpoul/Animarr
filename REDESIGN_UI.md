# Animarr — UI Redesign Migration Plan

> 2026-05-22. Migration plan from the current **Microsoft Fluent UI Blazor (v4)** look
> to the new visual language defined in [`design_handoff_animarr/`](design_handoff_animarr/README.md)
> (dark theme, crimson accent, always-on fanart backdrop, CJK watermark, full-bleed hero,
> Geist/Archivo Black typography).
>
> Companion to [REDESIGN.md](REDESIGN.md) — that doc covers the **UX/IA** redesign
> (page consolidation, auto-pilot flow, AI end-to-end identification). This one
> covers the **visual layer** (components, tokens, typography, motion).

---

## 0. Current state

### Stack
- **Server:** Blazor Server (.NET 10), `InteractiveServer` render mode.
- **Component library:** `Microsoft.FluentUI.AspNetCore.Components 4.14.1` (+ `…Icons`).
- **Styles:** Tailwind v4 (`Styles/app.css`) currently configured as a **Fluent helper**
  — re-exposes Fluent design tokens as Tailwind utilities (`bg-fluent-*`,
  `shadow-fluent-*`, etc.), explicitly **does not** import Tailwind preflight to
  avoid breaking Fluent's web-component padding/margin.
- **Theming:** `FluentDesignTheme` with `DesignThemeModes.System` + `OfficeColor`
  accent, stored in `localStorage` (`animarr-theme`, `animarr-accent`).
- **Backdrop:** already present — `<div id="backdrop-container">` in
  [MainLayout.razor:11](src/Animarr.Web/Components/Layout/MainLayout.razor) +
  [backdrop.js](src/Animarr.Web/wwwroot/backdrop.js) cross-fades fanart from DB.

### Fluent UI surface area in code
33 distinct Fluent components, **542 occurrences** across 11 files
(grep `<Fluent\w+`). Categorised:

| Category | Components | Replace? |
|---|---|---|
| **Layout / shell** | `FluentLayout`, `FluentNavMenu`, `FluentNavLink`, `FluentStack`, `FluentDivider` | Replace — visual identity drivers |
| **Primitive controls** | `FluentButton`, `FluentTextField`, `FluentNumberField`, `FluentSelect`/`FluentOption`, `FluentSwitch`, `FluentCheckbox`, `FluentRadio`/`FluentRadioGroup`, `FluentAnchor`, `FluentLabel`, `FluentBadge`, `FluentTab`/`FluentTabs` | Replace — most visible mismatch with new design |
| **Indicators** | `FluentProgress`, `FluentProgressRing` | Replace — design has bespoke progress visuals |
| **Icons** | `FluentIcon` + `Microsoft.FluentUI.…Icons` (Fluent System Icons) | Replace — design uses stroke-based inline SVG (see `components.jsx::Icon`) |
| **Complex widgets** | `FluentDataGrid`, `FluentPaginator`, `FluentDialog`+`Body`/`Header`/`Footer`, `FluentDialogProvider`, `FluentToastProvider`, `FluentTooltip`+`Provider`, `FluentDesignTheme` | **Keep** — re-skin via CSS variables and `::part()`; cost of rewriting outweighs benefit |

### Pages by Fluent density
| File | Fluent tags | Priority |
|---|---:|---|
| [Settings.razor](src/Animarr.Web/Components/Pages/Settings.razor) | 130 | P1 (worst regression risk if styling slips) |
| [Explorer.razor](src/Animarr.Web/Components/Pages/Explorer.razor) | 70+ | P1 |
| [Torrents.razor](src/Animarr.Web/Components/Pages/Torrents.razor) | 72 | P1 |
| [Home.razor](src/Animarr.Web/Components/Pages/Home.razor) | 46 | **P0** (this is the Catalog hero — biggest visual statement) |
| [MediaDetail.razor](src/Animarr.Web/Components/Pages/MediaDetail.razor) | 36 | **P0** |
| [History.razor](src/Animarr.Web/Components/Pages/History.razor) | 44 | P2 |
| Explorer panels, TorrentEdit, TorrentHistory, MainLayout | 10–80 each | P1/P2 |

---

## 1. Strategy: hybrid migration

**Keep from Fluent UI** (skin via CSS only, don't rewrite):
- `FluentDataGrid` + `FluentPaginator` — used heavily in Torrents/History; reimplementing virtualisation, sorting, and column resize is a multi-week trap.
- `FluentDialog`, `FluentDialogProvider`, `FluentDialogService` — modal stack + portal logic is non-trivial.
- `FluentToastProvider` + `IToastService` — Blazor-native, already wired into services.
- `FluentTooltip` + `FluentTooltipProvider` — positioning + collision detection.
- `FluentDesignTheme` — handles `prefers-color-scheme`, CSS variable injection, JS module loading. We just **override its CSS variables** with the design's oklch palette.

**Replace with custom** Razor components built on Tailwind v4 + design tokens:
- `FluentButton`, `FluentTextField`, `FluentNumberField`, `FluentSelect`, `FluentSwitch`, `FluentCheckbox`, `FluentRadio*`, `FluentTab*`, `FluentBadge`, `FluentLabel`, `FluentStack`, `FluentDivider`, `FluentAnchor`, `FluentNavMenu`/`FluentNavLink`, `FluentProgress`/`FluentProgressRing`, `FluentIcon`.

**Why this split:**
- The components we replace are the ones the design touches most often and where Fluent's "Office 365" geometry (4 px radius, Segoe UI, neutral grey) reads wrong on the new dark crimson palette. They are also the cheapest to rewrite.
- The ones we keep are workhorses where Fluent's behaviour (focus trap, ARIA, virtualisation) is the value, and they accept enough CSS-variable + `::part()` overrides to match the new dark theme without rewriting markup.

### Folder layout
```
src/Animarr.Web/
├── Components/
│   ├── Design/                ← NEW. House the custom design system.
│   │   ├── Primitives/        ← Button, Input, Switch, Tab, Badge, …
│   │   ├── Layout/            ← Sidebar, PageHeader, Hero, Container
│   │   ├── Media/             ← Poster, EpisodeCard, BackdropImage, CjkWatermark
│   │   ├── Icons/             ← Icon.razor (switch over name → inline <svg>)
│   │   └── _Imports.razor
│   ├── Layout/                ← existing — to be replaced by Design/Layout/AppShell
│   └── Pages/                 ← existing — migrated page-by-page
└── Styles/
    ├── tokens.css             ← NEW. CSS custom properties from design handoff.
    ├── app.css                ← existing, slimmed to: tokens + utilities + page-glue
    └── fluent-overrides.css   ← NEW. Re-skins kept Fluent widgets (DataGrid, Dialog).
```

---

## 2. Phases

### Phase 0 — Foundations (no UI change yet)
Goal: token plumbing + dev infra in place so subsequent phases can ship one page at a time without big-bang risk.

- **0.1** Add the design's CSS variable layer to a new `Styles/tokens.css`. Source of truth = [README §Design Tokens](design_handoff_animarr/README.md). Includes:
  - Backgrounds (`--bg-0/1`, `--surface`, `--surface-2/3`)
  - Borders (`--border`, `--border-strong`)
  - Text (`--text`, `--text-dim`, `--text-faint`)
  - Accent (`--accent`, `--accent-hi`, `--accent-soft`, `--accent-line`) — **oklch**, runtime-swappable via `--accent-hue` derivation
  - Semantic (`--success`, `--warn`, `--info`)
  - Spacing scale (4/6/8/10/12/14/16/18/22/26/36/48)
  - Radius scale (4/6/8/10/12/14/18/22)
  - Shadows + transitions
- **0.2** Wire fonts: import Geist + Archivo Black + Geist Mono + Noto Serif SC (use `wwwroot/lib/fonts/` self-host, not Google CDN — Docker-friendly, no telemetry leak). Set `--font-ui`, `--font-display`, `--font-mono`, `--font-cjk`.
- **0.3** Update `Styles/app.css` `@theme` to expose the new tokens as Tailwind utilities: `bg-surface`, `text-dim`, `border-strong`, `font-display`, `rounded-card`, `shadow-card`, …
- **0.4** Map `FluentDesignTheme`'s CSS variables onto ours in a new `Styles/fluent-overrides.css` so kept Fluent widgets pick up the new palette automatically:
  ```css
  :root, .fluent-design-theme {
    --neutral-layer-1: var(--bg-0);
    --neutral-layer-2: var(--bg-1);
    --neutral-layer-3: var(--surface);
    --neutral-foreground-rest: var(--text);
    --neutral-foreground-hint: var(--text-dim);
    --neutral-stroke-rest: var(--border);
    --accent-fill-rest: var(--accent);
    --accent-foreground-rest: var(--accent);
    /* …full list in 0.4 work item ticket */
  }
  ```
  Force dark mode permanently (design has no light variant) — set `Mode="DesignThemeModes.Dark"` and remove the system-theme switcher from Appearance settings.
- **0.5** Background plumbing: drop the film-grain overlay (SVG noise, `opacity: 0.06; mix-blend-mode: overlay`) into the existing `#backdrop-container`. CJK watermark utility class for hero blocks.

**Done when:** `dotnet run` boots, all existing pages render with the dark crimson palette through unmodified Fluent components. No layout change yet — only colours/fonts. Manually smoke-test Catalog, Torrents, Settings, MediaDetail.

### Phase 1 — Design primitives library
Goal: ship the custom components that will replace Fluent. Build in isolation in `Components/Design/Primitives/`. **No page migration yet.**

Build, in this order (each ≤ 1 component-file + matching scoped `.razor.css`):

1. **`Icon.razor`** — `name` parameter, big switch over the 30-odd icons in `components.jsx::Icon`. Stroke-based inline SVG, `currentColor`, `stroke-width: 1.6`. Eliminates the Fluent System Icons dependency at the JSX level — keep the package installed for kept widgets that reference it internally.
2. **`Btn.razor`** — variants `primary | solid | ghost | flat | danger`, sizes `sm | md | lg`. Maps to design's button language (see `components.jsx::Btn`).
3. **`Pill.razor`** — type ribbons, status chips. `tone="default|accent|success|warn|info"`, `dense` flag.
4. **`Field.razor` + `Input.razor` + `Select.razor`** — form primitives. `Field` wraps label + control + hint + error. Native `<input>`/`<select>` styled — no shadow DOM, no Fluent FAST quirks.
5. **`Toggle.razor`** — replaces `FluentSwitch`. CSS-only animation.
6. **`Tabs.razor` + `Tab.razor`** — segmented control + underline tabs (Catalog filter row uses segmented; MediaDetail uses underline). Two visual modes, one component.
7. **`Badge.razor`, `Divider.razor`, `Anchor.razor`** — trivial replacements.
8. **`ProgressBar.razor`, `ProgressRing.razor`** — animarr already has `.animarr-progress` CSS (see [Styles/app.css:243](src/Animarr.Web/Styles/app.css)). Wrap it into a component.

**Convention:** every Design component takes `class` + `style` passthrough and `@attributes` splat so consumers can add Tailwind utilities at call sites. Each is StaticSSR-compatible — interactivity only where genuinely needed (Tabs, Toggle).

**Done when:** a `/design` debug route (dev-only, behind `IWebHostEnvironment.IsDevelopment()`) renders the gallery of all primitives in all states. Visually compare against `Animarr v3 — Full Width.html` open in another browser tab.

### Phase 2 — Shell + backdrop
Goal: replace `MainLayout.razor` with the design's sidebar + always-on backdrop treatment. This is the cross-page chrome — must land before any page work because it's what the pages live inside.

- **2.1** New `Components/Design/Layout/AppShell.razor`. Replaces `FluentLayout`. Sidebar = custom `<aside>` with brand mark + nav, NOT `FluentNavMenu`. Routes: Catalog (`/`), Torrents (`/torrents`), Settings (`/settings`). Explorer + History move into Settings sub-tabs per [REDESIGN.md §2](REDESIGN.md). Sidebar shows `LIVE` badge on Torrents when any are downloading (already in state).
- **2.2** Backdrop layer: extend the existing `initGlobalBackdrop` JS to support the design's brighter hero treatment — same fanart, sharp + full-brightness, painted by the page hero on top of the blurred global layer. Cross-fade 1.4–1.8 s.
- **2.3** CJK watermark utility: `<CjkWatermark Text="@item.Cjk" Hue="@item.Hue" />` — fixed-position, vertical-rl, `text-orientation: upright`, 280–360 px, opacity 0.07–0.09.
- **2.4** Tweaks panel: **defer** to Phase 7. The design handoff includes it but it's dev-iteration tooling, not v0.3 user-facing UX.

**Done when:** sidebar matches the design, backdrop cross-fades on title change, dark mode is locked (`FluentDesignTheme Mode="Dark"`), film grain visible, no page content yet migrated.

### Phase 3 — Catalog (P0)
The visual flagship. Hero + filter bar + poster grid.

- **3.1** `Components/Design/Media/Hero.razor` — full-bleed fanart, layered gradients, type pills + tags pills, display title (UPPERCASE, Archivo Black 96–120 px), mono meta strip, overview, action row. Auto-rotate every `BackdropIntervalSec` (existing key). On rotate → push current fanart into the backdrop layer.
- **3.2** `Components/Design/Media/Poster.razor` — replaces inline poster markup in `Home.razor`. Color-mode wash tinted by `MediaItem.Hue`, CJK corner watermark, type ribbon, confidence badge when `conf < 0.85`, bottom title + meta block on dark gradient. Hover lift + accent shadow. Single source of truth — also used in NeedsReview popup and Edit drawer's poster picker.
- **3.3** Filter bar: replace the current section-tabs `FluentButton` row with the design's segmented control (Phase 1 `Tabs`) + search input (Phase 1 `Input`) + `Rescan all` ghost button + **NeedsReview chip** (count badge, pulsing dot, opens Phase 5 modal) + `{filtered}/{total} TITLES` mono counter. Second row: folder filter strip.
- **3.4** Grid: `display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 22px`. Already responsive — drop any fixed widths.
- **3.5** Remove `FluentLabel` H4 page title (the hero IS the page title).

**Done when:** Catalog matches `Animarr v3 — Full Width.html` pixel-for-pixel for: hero rotation, watermark, grid spacing, poster hover, filter row.

### Phase 4 — MediaDetail (P0)
- **4.1** Hero block — 68 vh / min 580 px. `BACK` glass-blur chip top-left (replaces current breadcrumb), `EDIT METADATA` chip top-right (currently buried in a button further down — promote to hero). Bottom row: 260 px poster + title block. H1 = UPPERCASE Archivo Black 96 px.
- **4.2** 3-column body grid (Synopsis 2.2 fr / Details 1 fr / Identification 280–360 px). Replace current `FluentStack` flex layout. Mono key/value lists in Details and Identification.
- **4.3** Episode cards: extract from inline `<style>` block in [MediaDetail.razor:18-72](src/Animarr.Web/Components/Pages/MediaDetail.razor) into `Components/Design/Media/EpisodeCard.razor`. Two states (ON DISK / MISSING) per the spec — 3 px left strip in `--success` / `--warn`, full-color vs grayscale thumbnail, ✓ vs ⚠ corner chip, play vs dashed-EMPTY hover. This is the single most-referenced design rule in the handoff; getting it right is non-negotiable.
- **4.4** Source buttons row (TMDB / MAL / IMDb with confidence color) — replace current `FluentAnchor` with custom `<Pill>` link variant.

**Done when:** episode card visualisation passes the on-disk-vs-missing visual diff against the handoff prototype.

### Phase 5 — EditMetadataDrawer + NeedsReview
- **5.1** Right-side drawer 560 px on desktop. **Build on `FluentDialog` with custom positioning** (Fluent supports modal positioning via `Position` attribute or CSS-only override) — don't reimplement the focus trap / portal. Skin the dialog surface to look like the design's drawer.
- **5.2** Six tabs (Source IDs · Basics · Poster · Backdrop · Tags · Manage). Use Phase 1 `Tabs` underline variant inside the drawer. Each tab is a thin Razor partial.
- **5.3** **Danger zone** on Manage tab — `Also delete files from disk` toggle (off by default) + two-step confirm. New UX: Animarr currently has no manual-correction surface; this drawer becomes the workhorse for fixing wrong matches and aligns with REDESIGN §1.6.
- **5.4** NeedsReview popup — same approach: `FluentDialog` for chrome + custom body. Lists folders + 2–3 candidate cards per folder + `+ Search manually`. Triggered from Catalog filter bar's NR chip (Phase 3.3).

### Phase 6 — Torrents + Settings + History
P1 pages. Less visual punch, denser data.

- **6.1 Torrents** — keep `FluentDataGrid` (reskinned via `fluent-overrides.css`). Replace the header row's `FluentButton`/`FluentTabs` with Phase 1 primitives. Live counter strip (DOWN MB/s accent · UP MB/s success). Per-row left strip + state dot.
- **6.2 AddTorrent drawer** — right-side drawer 480 px, same `FluentDialog`-based pattern as 5.1. Field layout per [README §Torrents](design_handoff_animarr/README.md#4-torrents): File/Magnet toggle → drop zone → per-file priority chips → **destination dropdown listing identified titles, not raw folder names**. Aligns with REDESIGN §1.6's tree-picker but ships flat first; tree comes later.
- **6.3 Settings** — vertical-tab layout. Sticky 220 px tab column on the left (custom — `FluentTabs` doesn't do vertical well at this density). Tabs: General · Root folders · Rename history · Appearance · AI/LLM · Patterns · Ignore rules · Torrent · Metadata. Most of these already have backing Razor; just reshell them.
- **6.4 Rename history** — already exists ([History.razor](src/Animarr.Web/Components/Pages/History.razor)). Move under Settings (per [REDESIGN.md §2.1](REDESIGN.md)), reshell to the 4-KPI-tile + grouped timeline design.
- **6.5 Appearance tab** — accent swatches (5 colours: crimson / amber / green / blue / violet), backdrop on/off + rotation + blur + brightness sliders. Already has backing config keys (`BackdropEnabled`, `BackdropIntervalSec`, `BackdropBlurPx`, `BackdropBrightness`).
- **6.6** Remove Fluent's accent picker (`OfficeColor` enum-based) — replace with the design's oklch swatch row. Wire `--accent-hue` so all accent variants recompute at runtime.

### Phase 7 — Polish + cleanup
- **7.1** Tweaks panel (the floating right-anchored dev tool from `tweaks-panel.jsx`) — **decide:** ship under Settings → Appearance with the same sliders, or hide behind dev-only flag. Recommend: fold its controls into Appearance, drop the floating UI.
- **7.2** Mobile / PWA — out of scope for v0.3. Document responsive breakpoints in the design system but defer the mobile-specific surfaces (bottom tab bar, bottom sheets) to a later milestone. The current Blazor Server app would need `Microsoft.AspNetCore.Components.WebAssembly` or a Service Worker before "PWA" means anything offline.
- **7.3** Remove `Microsoft.FluentUI.AspNetCore.Components.Icons` package once `FluentIcon` call sites are all migrated to `<Icon name="…" />`.
- **7.4** Audit remaining `FluentLabel` Typo usages — replace with semantic HTML (`<h1>` … `<h6>`) styled by tokens. `Typography.H4` etc. is a Fluent-ism that doesn't map cleanly to the design's display scale.
- **7.5** Delete dead CSS in [Styles/app.css](src/Animarr.Web/Styles/app.css) (FluentUI `@theme` block, `fluent-text-field` outline overrides, `.folder-combobox` width hack — once the custom `Select` ships).

---

## 3. Risks and watch-outs

### 3.1 Shadow DOM + CSS variable inheritance
Fluent's FAST web components inherit CSS custom properties through shadow DOM boundaries (see the existing `--tw-ring-offset-color: transparent` workaround in [app.css:144](src/Animarr.Web/Styles/app.css)). When we map our tokens onto `--neutral-*` and `--accent-*`, the kept widgets pick them up — but only at the **shadow boundary**, so deep `::part()` selectors are still needed for internal element styling (e.g. data-grid row borders, dialog body background). Allocate time to iterate on `fluent-overrides.css`.

### 3.2 Backdrop layer z-index
The design puts a fixed-position blurred fanart slideshow at `z-index: 0` under everything. The current setup already does this but Fluent's dialog/toast/tooltip providers all create their own portals at `z-index: 9999+`. Verify modals stack correctly above the backdrop and that the **hero block paints the same fanart sharp on top** — needs a second `position: absolute` layer scoped to the hero, not a second fixed-position element.

### 3.3 Cross-fade memory growth
Open question from the design handoff: "prune to last 2" backdrop layers. The current `backdrop.js` already alternates between `#backdrop-slide-a` and `#backdrop-slide-b` (only two layers ever). Confirm this still holds when hero blocks add their own per-page sharp layer.

### 3.4 Theme switching legacy
`ThemeService.Mode` and `ThemeService.AccentColor` are wired into `localStorage` + `FluentDesignTheme`. After Phase 6.6, accent becomes oklch-based and mode is locked dark — `ThemeService` shrinks to "current accent hue" only. Don't remove the service entirely; the Appearance tab still needs a reactive surface to bind sliders to.

### 3.5 Fluent component leakage
After Phase 7, **periodically grep**: `<Fluent\w+` in `Components/Pages/` and `Components/Explorer/`. Some are easy to miss inside `@if`/`@foreach` branches or inside Razor `RenderFragment` parameters. The kept ones (`FluentDataGrid`, `FluentDialog`, `FluentToast`, `FluentTooltip`) should be the **only** survivors — write a sanity-check test or just add a TODO to README.

### 3.6 Touch / hover affordances
Design specifies hover-only states (poster lift, EMPTY dashed overlay on missing episode). On touch devices these collapse. Defer to mobile phase but at minimum: use `@media (hover: hover)` to gate the hover-only treatments so they don't trigger sticky-hover on tap.

---

## 4. Execution order summary

```
Phase 0  Foundations (tokens, fonts, Fluent re-skin)        ← invisible, prep
Phase 1  Design primitives library                          ← /design gallery
Phase 2  AppShell + backdrop + CJK watermark                ← chrome
Phase 3  Catalog (hero, posters, filter bar)                ← P0
Phase 4  MediaDetail (hero, 3-col body, episode card)       ← P0
Phase 5  EditMetadataDrawer + NeedsReview popup             ← new UX
Phase 6  Torrents, AddTorrent, Settings, History            ← P1
Phase 7  Polish, Tweaks integration, cleanup, dead CSS      ← ship-ready
```

Each phase ships independently behind no feature flag — Animarr is single-user
self-hosted, so users update at their own cadence and a half-migrated state for
a release cycle is acceptable as long as the visual mismatch is contained to
one page.

---

## 5. Open questions

- **`FluentDesignTheme` removal?** Keeping it for the CSS-variable bootstrap is convenient but means shipping ~80 KB of Fluent JS we don't otherwise use after Phase 7. Alternative: hand-roll a small `<style>` block that sets the mapped variables and drop `FluentDesignTheme` entirely. Decide at end of Phase 6.
- **Tweaks panel ship/cut.** Per §7.1 — recommend cut from end-user UI, fold sliders into Appearance.
- **Mobile target.** PWA via Blazor Server has limitations (no offline, no install-on-iOS without workarounds). MAUI native is a separate project. Defer.
- **WinUI 3 client.** The design handoff notes WinUI 3 as a secondary desktop client. This redesign plan is **web-only**. WinUI 3 would be a separate XAML-based reimplementation — same tokens, different component layer entirely. Note: WinUI 3 XAML compiler crashes silently (MSB3073 with no diagnostic) when a property is set on a type that doesn't define it — relevant when porting tokens to `App.xaml` resources.
