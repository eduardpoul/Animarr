# Animarr v0.3.0

The biggest release yet — ~70 commits since v0.2.0. Animarr was re-architected
from a single Razor server into an **API + shared UI + WebAssembly client +
native app**, gained **multi-user accounts**, **in-browser & native playback**,
a **built-in LLM**, and a full **watch-tracking** experience (Continue / Up Next
/ autoplay).

## 🏗️ Re-architecture

- Split the old Razor monolith into an **API-only server** (`Animarr.Web`) + a
  shared Razor class library (`Animarr.UI`) + a **Blazor WebAssembly** client
  (`Animarr.Web.Client`) + a **.NET MAUI hybrid** app (`Animarr.App`, for
  Android / Android TV / iOS / desktop).
- WASM static-asset hosting with fingerprinting; Tailwind build moved into
  `Animarr.UI`; Dockerfile rebuilt around the new layout.

## 👥 Multi-user & devices

- **Accounts with cookie auth + a first-run setup wizard.** Roles and per-folder
  visibility scope what each user sees.
- **Profile switching** ("who's watching") and **PIN login**.
- **Device pairing** for TVs / phones and a **multi-server** registry (connect
  to more than one Animarr instance).

## ▶️ Playback

- **In-browser HLS player** with a custom HUD (two-row controls, scrim,
  tap-to-toggle, autostart, readable glyphs) and a **quality ladder** with GPU
  downscale.
- **Multi-vendor hardware acceleration** — VAAPI (AMD/Intel) and NVENC (NVIDIA),
  auto-detected by the deploy script. One image, any GPU.
- **DLNA cast** to TVs, **audio-sync offset** control, and ultrawide
  letterbox auto-crop.
- **Android TV**: native ExoPlayer path, TV-remote navigation, and the Google-TV
  "Watch Next / Continue watching" tile with deep links.
- MAUI playback hardening — loopback media proxy (fixes phone playback freeze),
  HTTPS-page/HTTP-server mixed-content fix, surface-allocation deadlock fix, and
  auth cookies persisted across restarts.

## 🧠 Built-in LLM

- **Embedded llama.cpp provider** — runs a local LLM *inside the container* (no
  external Ollama required), with **Vulkan GPU** offload and a lazy idle
  lifecycle that spins the model down when unused. GGUF models download to
  `/app/data/models`.

## 📺 Watch tracking — Continue, Up Next & autoplay

- **Continue Watching hero** + catalog watch state (progress bars, ✓ watched,
  90% auto-watched).
- **Per-episode thumbnails** (TMDB still, with an ffmpeg frame-grab fallback).
- **"Mark earlier episodes too?" popup** — marking an episode watched offers to
  backfill earlier unwatched on-disk episodes across all seasons.
- **Up Next** rail on Home, also folded into the hero: the next unwatched
  episode per series — including freshly-landed new episodes (badged "New
  episode"). Clicking a card jumps straight into playback.
- **End-of-episode autoplay** — at 90% the player shows an "Up Next" card
  (Play next / Dismiss) with a ~10s countdown auto-advance; Next/Prev roll
  across season boundaries.
- The MediaDetail **Continue** CTA now resolves the actual next unwatched
  episode (no longer "Play first episode" once you've watched everything but the
  finale).

## 🎨 UI / themes

- **v5 redesign** with **10 selectable themes**, a refreshed full-bleed hero,
  glass chrome, a mobile shell, and PWA install.
- Season/episode grids, a real 3-source metadata panel (TMDb / MAL / IMDb with
  backend-honoured toggles), TopBar folder pills that filter hero + grid
  together, and broad Settings hardening.

## 🧲 Torrents

- File-selection tree, a smarter destination picker (shows identified library
  titles), an Add drawer with Magnet / `.torrent` tabs, a progress-stuck-at-100%
  fix, and robust custom root-folder remapping on add and restore.

## 🗂️ Episode resolution

- Three-tier file → (season, episode) mapping: deterministic filename parsing +
  LLM + manual override, with absolute-episode handling for donghua whose disk
  layout differs from TMDB's single-season numbering.

## 📦 Docker image

```
docker pull ghcr.io/eduardpoul/animarr:0.3.0
docker pull ghcr.io/eduardpoul/animarr:latest
```
