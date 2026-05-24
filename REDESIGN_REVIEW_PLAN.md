# Design Review Plan — vs CANVAS.html

> Source of truth: [`design_handoff_animarr/CANVAS.html`](design_handoff_animarr/CANVAS.html)
> Method: walk every section of CANVAS, compare my implementation, fix divergences.
> Each item flips ✅ when it matches; otherwise note the fix needed.

---

## Phase A — Design tokens & primitives

### A.1 Color tokens (CANVAS §01.0)
- [ ] `--bg-0` `#0a0807`
- [ ] `--bg-1` `#100d0b`
- [ ] `--surface` `#15110e`
- [ ] `--surface-2` `#1c1814`
- [ ] `--surface-3` `#25201b`
- [ ] `--border` `rgba(255, 240, 220, 0.07)`
- [ ] `--border-strong` `rgba(255, 240, 220, 0.14)`
- [ ] `--accent` `oklch(0.66 0.20 25)`
- [ ] `--accent-hi` `oklch(0.74 0.21 25)`
- [ ] `--accent-soft` `oklch(0.66 0.20 25 / 0.16)`
- [ ] `--accent-line` `oklch(0.66 0.20 25 / 0.40)`
- [ ] `--success` `oklch(0.74 0.15 150)`
- [ ] `--warn` `oklch(0.80 0.17 75)`
- [ ] `--info` `oklch(0.74 0.10 240)`
- [ ] `--text` `#f4eee6`
- [ ] `--text-dim` `#a8a097`
- [ ] `--text-faint` `#5d564f`

### A.2 Accent presets (§01.2) — **5 colors**, not 6
- [ ] crimson · amber · green · blue · violet (in this exact order)

### A.3 Type scale (§01.3)
- [ ] Display XL — Archivo Black 96-120px / line-height 0.86-0.88 / tracking -2 to -3.4
- [ ] Display L — Archivo Black 44px / 0.95 / -1
- [ ] Display M — Archivo Black 22px / -0.4 (card titles, e.g. "EDIT METADATA")
- [ ] UI body — Geist 13-15px / 1.55-1.65
- [ ] UI meta — Geist Mono 11-12.5px / tracking 0.6-1.4 / uppercase
- [ ] Decorative CJK — Noto Serif SC 60-360px / opacity 0.07-0.55

### A.4 Spacing scale (§01.4): 4/6/8/10/12/14/16/18/22/26/36/48
- [ ] Tokens match (already in tokens.css)

### A.5 Radius scale (§01.5) — **fix order**:
- [ ] 4 → badges
- [ ] **6 → controls (buttons, inputs)**
- [ ] **8 → chips (pills with bg, info boxes)**
- [ ] 10 → cards
- [ ] 14 → panels
- [ ] 18 → hero card
- [ ] 22 → modal
- [ ] 9999 → pill (circular)

Current code has `--radius-chip: 6px; --radius-ctrl: 8px;` — **swapped**.

### A.6 Component inventory (§01.6)

#### A.6.1 Buttons — 5 variants
- [ ] **primary** — solid accent, white text
- [ ] **solid** — surface-2 bg, text on top, border-strong
- [ ] **ghost** — transparent, border-strong, text-dim
- [ ] **flat** — no border, no bg, text-dim
- [ ] **danger** — transparent, crimson border + text

#### A.6.2 Pills — 5 tones
- [ ] **neutral** — bg rgba(255,255,255,0.05), text-dim, border
- [ ] **accent** — accent-soft bg, accent-hi text, accent-line border
- [ ] **success** — oklch(.74 .15 150 / .15), success text, border 35%
- [ ] **warn** — oklch(.80 .17 75 / .15), warn text, border 40%
- [ ] **count** — same as neutral (used for "12 watchers")

Font: mono 10px, letter-spacing 0.6px, uppercase, padding 3px 7px, radius 4.

#### A.6.3 Toggle
- [ ] 34×18 track, 14×14 thumb, accent fill + shadow when on

#### A.6.4 Poster card
- [ ] Image bg with `saturate(.85) brightness(.85)`
- [ ] CJK watermark vertical-rl top-right
- [ ] Type ribbon (uppercase mono pill) top-left
- [ ] Bottom title block: Archivo Black UPPERCASE title + mono meta line "year · N EP · ★ rating"
- [ ] Hover: translateY(-3px), accent shadow

#### A.6.5 Episode card — status visualisation
- [ ] **ON DISK**: full color, 3px success left strip + glow, ✓ chip, play overlay on hover
- [ ] **MISSING**: opacity 0.45-0.55, grayscale thumb, 3px warn dashed strip, ⚠ chip, EMPTY dashed overlay on hover

#### A.6.6 Sidebar
- [ ] 230px width
- [ ] Brand row: 28px square logo (gradient accent→darker accent) + ANIMARR display + "v2.0 · LOCAL" mono
- [ ] Nav items: icon + label + optional count badge (right-aligned mono pill)
- [ ] LIVE badge for Torrents row: green dot + "LIVE" mono
- [ ] Bottom: LLM status card

#### A.6.7 Status edges
- [ ] On-disk row: 3px success left strip + glow shadow
- [ ] Missing row: 3px warn left strip, opacity 0.55, text-dim

#### A.6.8 Backdrop layer
- [ ] Always-on fixed `blur(14px) brightness(38%) saturate(0.95)` + vignette + hue tint

### A.7 Icon set (§01.7) — 18 stroke-based SVGs
- [ ] catalog · torrent · folder · clock · settings · search · play · plus · check · x · warn · magic · pencil · trash · refresh · undo · external · filter

---

## Phase B — Desktop pages (20)

For each: open `design_handoff_animarr/pages/desktop/NN-*.html`, screenshot my live deploy, diff.

- [ ] 01-catalog
- [ ] 02-catalog-needs-review (NR modal triggered)
- [ ] 03-media-detail
- [ ] 04-media-detail-edit-ids (drawer: Source IDs tab)
- [ ] 05-media-detail-edit-basics
- [ ] 06-media-detail-edit-poster
- [ ] 07-media-detail-edit-backdrop
- [ ] 08-media-detail-edit-tags
- [ ] 09-media-detail-edit-manage
- [ ] 10-torrents
- [ ] 11-torrents-add (drawer)
- [ ] 12-settings-general
- [ ] 13-settings-folders
- [ ] 14-settings-history
- [ ] 15-settings-appearance
- [ ] 16-settings-llm
- [ ] 17-settings-patterns
- [ ] 18-settings-ignore
- [ ] 19-settings-torrent
- [ ] 20-settings-metadata

---

## Execution order

1. Fix tokens (A.1–A.7) — most-pervasive, smallest blast radius
2. Walk pages 01→20 in order, fixing what differs from each reference HTML
3. Final build + deploy, smoke-test all routes
