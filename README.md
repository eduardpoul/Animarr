# Animarr

**Animarr is a personal media library for your own collection of anime, donghua, films and series.** Point it at the folders where your files already live and it turns that pile of cryptically‑named downloads into a polished, browsable library — a wall of posters, fanart backdrops, Continue Watching, season and episode lists — that you actually *watch* from, in the browser, on your phone, or on the TV.

Think of it as your own streaming service over your own files: nothing moves on disk, but on screen you get covers, descriptions, ratings, episode grids and one‑click playback instead of a file manager.

## Where the metadata comes from

To turn a folder into a real catalog entry, Animarr identifies each title against three sources in parallel and cross‑checks them against each other:

- **TMDB** — posters, backdrops, descriptions, seasons & episodes, ratings. The primary source for films and series.
- **MAL** (MyAnimeList) — anime‑focused matching and episode counts.
- **IMDb** — extra cross‑validation and a fallback when TMDB/MAL come up short.

On top of that it pulls **anime opening/ending themes** from AnimeThemes.moe, and discovers **sideloaded dub / subtitle tracks** sitting next to your videos.

## Why there's an LLM

Real folder and file names are a mess — `[SubsPlease] Doupo.Cangqiong.S05.1080p`, pinyin, romaji, Cyrillic dub tags, scene‑release soup. A plain title search chokes on all of that. So Animarr puts a language model in front of the search to:

1. **Read the real title and year** out of the noise and produce a clean, searchable name (e.g. a canonical English title for a Chinese or Japanese show).
2. **Pick the right match** when the sources return several plausible candidates.
3. **Place loose files into episodes** when their names don't follow any recognizable pattern.

This is what makes non‑English content — most donghua, untranslated anime, transliterated Russian dubs — actually identify. Cleanly‑named English shows like `Breaking Bad (2008)` work fine without it.

## Use it your way

- **LLM: built‑in, external, or none.** Run the bundled **llama.cpp** model right inside the container (downloads once, CPU‑only by default, optional GPU) — no extra service to install. Or point Animarr at any **OpenAI‑compatible endpoint** (Ollama, LM Studio, Groq, OpenAI, …). Or skip the LLM entirely and rely on plain regex parsing for cleanly‑named files. See [AI / LLM setup](#ai--llm-setup).
- **Watch anywhere.** A Docker **web app**, plus **native apps** for Android phone, **Android TV** (D‑pad / leanback) and Windows. The apps auto‑discover your server on the LAN and pair to the TV by QR code or a 6‑digit code.
- **Get media in.** A built‑in **BitTorrent client** (magnet or `.torrent`) drops finished downloads straight into the right library folder.
- **Safe by default.** Animarr **never renames or moves your files.** Every match updates the database only — your folder tree stays exactly as you laid it out.

## Features

- **AI‑driven identification** — LLM normalises the title, then cross‑validates across **TMDB**, **MAL** and **IMDb** in parallel. Matches below the auto‑apply threshold drop into a **Needs Review** banner with poster thumbnails + "open source" links so you pick the right one in a click.
- **Built‑in LLM** — embedded llama.cpp server with a curated model catalog (Qwen2.5 0.5B/1.5B/3B, Llama 3.2 3B, or any custom Hugging Face GGUF). Downloads on demand, runs in‑container, optional Vulkan GPU offload. Or use any external OpenAI‑compatible endpoint.
- **Never touches your files** — identification only updates DB associations; nothing on disk is renamed or moved.
- **Smart episode resolution** — files are mapped to (season, episode) by a layered resolver: deterministic regex/path parsing → optional one‑click **Resolve with AI** for files that don't parse → manual per‑file override. Split‑season donghua (one absolute TMDB season spread across several disk folders) is handled with automatic season offsets.
- **Media catalog** — poster grid with a fanart backdrop hero, **Continue Watching** / **Next Up**, a compact library block on Home (full grid on `/library`), detail pages with seasons/episodes, ratings, tags and external links. The Home section order + visibility is per‑user (drag to reorder in Profile).
- **Recommendations & watchlist** — heuristic **"For you"** and **"More like this"** rails, local library first with a TMDB backfill (scope is per‑user: everywhere vs library‑only). A **"Хочу посмотреть" / watchlist** collects titles you want to get to; dismiss anything you're not interested in.
- **Airing calendar** — a week view (`/calendar`) of when the next episodes of your ongoing titles drop (AniList/TMDB schedule), plus a **"This week"** rail on Home. Handles the donghua case where a title airs weekly under a later entry than the one it matched.
- **Franchises / watch order** — on a title page, a **release‑order rail** of the whole franchise with "you are here", watched ticks, and a **Want** button on parts you don't own yet. Built from **AniList relations** (anime — sequels/prequels/side‑stories) *and* **TMDB collections** (live‑action / film series like Mission: Impossible), merged.
- **Filler markers** — episodes MAL flags as **filler / recap** (via Jikan) get a chip in the episode grid & list; a per‑title (or global) **hide fillers** toggle drops them from the list and **skips them on next‑episode advance**.
- **Personal statistics** — a `/stats` dashboard: hours watched, episodes/titles, top genres & studios, most‑watched titles, a type split, a GitHub‑style **activity heatmap**, streaks and hours‑per‑month.
- **Metadata language** — fetch titles, overviews and genres from TMDB in your language; switching the language re‑localizes the whole library in the background (with a progress bar), prefers localized posters, and falls back to English per field when there's no translation.
- **In‑browser & native playback** — a custom player streams your files right in the browser (and via native ExoPlayer on Android TV), keeping the **original video bitstream and HDR** whenever the client can decode them; it only re‑encodes (hardware‑accelerated) as a last resort. See [Playback](#playback).
- **Skip intro & credits** — opening and ending segments are auto‑detected (AniSkip → embedded chapters → audio fingerprint → black‑frame cascade); a **Skip** button appears over the intro, and an **Up Next** card at the credits auto‑advances to the next episode.
- **Categories** — items are auto‑classified by the LLM at identification time into category chips on the home screen; you can pin categories manually per title.
- **Full‑text search** — instant client‑side filter across title / original / English / CJK names.
- **Theme music** — fetches the anime OP/ED from [AnimeThemes.moe](https://animethemes.moe) and plays it on the detail page (per‑user opt‑in + volume).
- **External audio / subtitle tracks** — discovers sideloaded dubs and subtitle files (`.mka`, `.srt`, `.ass`) sitting next to your video and offers them alongside the embedded tracks.
- **Section folders** — point Animarr at a root directory and each subfolder is auto‑imported as a separately‑watched media folder. **Flat sections** (one video file per movie, no subfolder) are supported too.
- **Torrent client** — MonoTorrent‑based: add by magnet or `.torrent`, per‑file priority (incl. skip), per‑torrent + global speed limits (Mbps), create‑subfolder, flatten subfolders, strip/rename the root folder. The destination picker shows your **identified library titles**, not raw on‑disk names.
- **Native apps** — Android phone, **Android TV** (D‑pad / leanback), and Windows (.NET MAUI). Auto‑discovers servers on your LAN (mDNS + subnet probe) and pairs a TV to your account by **QR code or 6‑digit code**.
- **Multi‑user** — roles & permissions (view / upload / system settings / manage users), fast per‑device user switch with optional PIN.
- **Multi‑server** — register several Animarr servers and switch between them from the profile menu.
- **Themeable, multi‑language UI** — five interface languages (English, Русский, Українська, Deutsch, Español), five palettes + accent colour, animated backdrop toggle.
- **Persistent state** — SQLite DB, MonoTorrent fastresume, image cache and encryption keys live in `/app/data` and survive restarts. Images stay **out of your media tree**.

## Quick start

### 1. `docker-compose.yml`

```yaml
services:
  animarr:
    image: ghcr.io/eduardpoul/animarr:latest
    container_name: animarr
    restart: unless-stopped
    ports:
      - "8450:8080"      # Web UI
      - "6881:6881"      # Torrent (TCP)
      - "6881:6881/udp"  # Torrent (UDP)
    environment:
      - TZ=UTC
    volumes:
      - animarr-data:/app/data
      - /your/media/path:/media:rw

volumes:
  animarr-data:
```

Or copy `docker-compose.yml` from the repo root and adjust the media bind mount.

### 2. Start

```bash
docker compose up -d
```

### 3. Open the UI

```
http://localhost:8450
```

### 4. Set up the LLM

In **Server settings → AI / LLM**, either pick a **built‑in** model (downloads automatically) or point at an external OpenAI‑compatible endpoint. See [below](#ai--llm-setup). This is the single highest‑leverage step for anything non‑English.

### 5. Add a media folder

In **Server settings → Folders**, add a watched folder (use the **container‑side** path, e.g. `/media/Anime`). Mark it a **section** to auto‑import each subfolder as its own title.

## Volumes

| Mount | Purpose |
|-------|---------|
| `animarr-data:/app/data` | SQLite database, downloaded LLM models (`/app/data/models`), image cache (`/app/data/image-cache`), MonoTorrent fastresume, DataProtection keys. **Required for persistence.** |
| `/your/media/path:/media:rw` | Your media library. Add as many bind mounts as you like — use the container‑side path when configuring a folder in Animarr. Per‑title assets (theme music, etc.) are written to a hidden `.animarr/` subfolder next to the media; posters/backdrops stay in `/app/data/image-cache`, never in your tree. |

## Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `TZ` | `UTC` | Timezone for log timestamps, e.g. `Europe/Moscow`, `America/New_York`. |
| `ANIMARR_LLM_VULKAN` | *(unset)* | Set to `1` to let the built‑in LLM use the GPU via Vulkan. On first boot the container installs the Vulkan userspace drivers (cached on the data volume). Pair with a `/dev/dri` device mount + `video` group (see compose comments). NVIDIA: use the nvidia‑container‑toolkit. |
| `AppSettings__WatcherDelayMs` | `2000` | Milliseconds the folder watcher waits after a file appears before processing it. |

Any `AppSettings__*` key can be overridden via environment (standard ASP.NET configuration).

## AI / LLM setup

Configured at runtime in **Server settings → AI / LLM** — no restart needed.

### Option A — Built‑in model (recommended, zero external setup)

Animarr ships a llama.cpp runtime. Choose a model from the catalog and it downloads once to `/app/data/models` and runs inside the container.

| Model | Size on disk | RAM | Notes |
|---|---|---|---|
| Qwen2.5 0.5B (Q4) | ~0.5 GB | ~1 GB | Fallback only — misses many CJK / transliterated titles. |
| **Qwen2.5 1.5B (Q4)** | ~1 GB | ~2 GB | **Recommended.** Reliable on pinyin/romaji, clean JSON, solid year extraction. |
| Qwen2.5 3B (Q4) | ~1.9 GB | ~4 GB | Better on ambiguous franchise names; happier on a GPU. |
| Llama 3.2 3B (Q4) | ~2 GB | ~4 GB | Strong multilingual handling. |
| *Custom* | — | — | Paste any Hugging Face `org/repo` + `.gguf` filename. |

CPU‑only works out of the box. For GPU, set `ANIMARR_LLM_VULKAN=1` and expose `/dev/dri` (AMD/Intel) — see the compose comments. The embedded server starts on demand and stops after an idle timeout to free memory.

### Option B — External OpenAI‑compatible endpoint

Point Animarr at any `/v1/chat/completions` endpoint:

| Provider | Notes |
|---|---|
| **Ollama** (local) | Free, runs on your hardware, no API key. `ollama pull qwen2.5:1.5b`, base URL `http://<host>:11434/v1`. |
| **LM Studio** | Local GUI, OpenAI‑compatible server. |
| **OpenAI / Groq / Together / …** | Any compatible cloud endpoint (API key required). |

Fields: **Base URL**, **Model**, **API Key** (leave empty for local).

### What the LLM does

1. **Normalises titles** — `Doupo Gangqiong` → `Battle Through the Heavens`. Search runs against the English form when present.
2. **Extracts year & type** from the path; a year‑anchored filter stops a 1968 film matching a 2025 file.
3. **Picks the best candidate** when TMDB/MAL/IMDb return several plausible results.
4. **Maps loose files to episodes** on demand when filenames don't parse (the "Resolve with AI" button).

Without an LLM, Animarr falls back to pure regex/path parsing — fine for `Show.Name.S01E02.1080p.mkv`, weak on anything non‑Latin.

## How files become episodes

Animarr **reads** season/episode from your files; it never rewrites them. Resolution is layered, highest precedence last:

1. **Deterministic parse** — regex patterns with named groups, season folders (`Season 2`, `S02`, `Part 3`, `Specials` → S0), and bare‑number filenames (`12.mkv` → episode 12). Single‑season shows default a seasonless‑but‑numbered file to Season 1.
2. **AI resolution** *(optional)* — for files the parser can't place, **Resolve with AI** matches them against the TMDB episode list.
3. **Manual override** — set season/episode by hand per file in the detail page's **Unmatched files** panel.
4. **Season offsets** — when a donghua is one long TMDB season but split into `Season 1/2/3…` folders on disk, Animarr aligns them automatically and shows the absolute episode number.

### Parsing patterns

Patterns are .NET regular expressions with named capture groups, used purely to **extract** numbers (never to rename):

| Group | Meaning |
|-------|---------|
| `season` | Season number (optional) |
| `episode` | Episode number |
| `title` | Override show title from the filename (optional) |

Example for `[Group] Show Name - 12 [1080p].mkv`:
```
\[.+?\]\s*(?P<title>.+?)\s*-\s*(?P<episode>\d+)
```

Patterns have a **priority** (lower = checked first) and a **scope**: **global** (all folders of a type) or a **per‑folder override** (which can also *exclude* a global pattern for that folder). Managed in **Server settings → Parsing**.

## Library & catalog

- **Home** — fanart hero (Continue Watching, falling back to top‑rated), category chips, poster grid.
- **Categories** — LLM‑assigned at identification; curate the list in **Server settings → Categories**, or pin per title in **Edit metadata → Categories**.
- **Search** (`/search`) — instant filter across all title forms.
- **Detail page** — hero, season tabs + episode grid (or a movie card), studio/runtime/rating/tags, TMDB/MAL/IMDb links, theme music, and the **Unmatched files** resolver.
- **Edit metadata** drawer — tabs for **Source IDs** (paste a TMDB/MAL/IMDb URL or id and re‑identify), **Basics**, **Poster** & **Backdrop** (all‑language galleries — pick any artwork you like), **Tags**, **Categories**, and **Manage** (re‑identify / delete from catalog). A read‑only line shows the original on‑disk folder/file the scanner matched, so a wrong match is easy to spot.

## Playback

Animarr plays your files directly in the browser — no companion app required — and picks the lightest delivery path the client can handle, so original quality is preserved whenever possible:

- **Direct Play** — browser‑friendly MP4 (H.264 / HEVC + AAC) streams as the raw file: instant start, perfect A/V sync, zero transcoding.
- **Direct Stream** — MKV and other containers are **remuxed on the fly** to a native MP4 stream (video copied untouched, audio to AAC), so the **original video bitstream and HDR pass through unchanged**. Browser HDR output is more reliable on this native path than via MSE.
- **HLS** — only when the browser genuinely can't decode the codec (or you cap the quality) does Animarr re‑encode, **hardware‑accelerated** via VAAPI (AMD/Intel) or NVENC (NVIDIA), auto‑detected at deploy.

The player has a custom HUD (two‑row controls, scrim, tap‑to‑toggle), a **quality menu** with bitrate presets, embedded **+ sideloaded audio / subtitle** track switching, an **audio‑sync** offset control, ultrawide letterbox auto‑crop, **DLNA cast** to a TV, and **Skip intro / Skip credits** buttons with an **Up Next** card that auto‑advances at the end. On **phones** the controls switch to a touch layout — a big centre play/pause, **double‑tap the left/right edge to skip ±10s**, a full‑width scrubber, and a settings gear that holds the secondary controls. Android TV gets a native **ExoPlayer** path with D‑pad navigation. The MEDIA INFO panel reports exactly what's playing — codec, bit depth, HDR format, and which delivery path is in use.

## Torrent client

- Add by **magnet** or **.torrent** (File / Magnet toggle, File first).
- The destination picker shows your **identified library titles** ("Bleach", "Cowboy Bebop"), not raw folder names.
- **Per‑file priority** — Normal / High / Low / Skip, choose what to download before it starts.
- **Create subfolder** (`+`) inside a destination without leaving the panel.
- **Flatten subfolders** and **strip / rename the root folder** so files land where you want.
- Speed limits in **Mbps** — global (Server settings → Downloads) and per‑torrent.
- On completion the destination folder is rescanned and identified — **no files are renamed**.

## Native apps (Android phone / TV / Windows)

The native build adds, over the browser app:

- **Server discovery** — finds Animarr servers on your LAN via mDNS, with a subnet TCP probe fallback for tricky home networks; or add a server by IP / hostname / Tailscale name.
- **TV mode** — leanback layout, full **D‑pad spatial navigation**, and a sign‑in flow that **pairs the TV to your account** by scanning a QR code or typing a 6‑digit code from your phone.
- Built for `android-arm64`, `android-arm` (32‑bit budget TV boxes), Windows, and Apple targets.

TV mode is also available in the browser (toggle in Profile → Appearance) and is auto‑detected on leanback devices.

## Building from source

```bash
git clone https://github.com/eduardpoul/Animarr
cd Animarr
docker build -t animarr:latest .
docker compose up -d
```

Or with the .NET 10 SDK + Node.js (for the Tailwind build):

```bash
cd src/Animarr.Web
dotnet run
```

Solution layout:

| Project | Role |
|---------|------|
| `Animarr.Web` | ASP.NET Core server — API, identification pipeline, torrent engine, embedded LLM, static hosting. |
| `Animarr.UI` | Blazor Razor Class Library — all shared UI (pages, components, styles). |
| `Animarr.Web.Client` | Blazor WebAssembly host for the browser app. |
| `Animarr.App` | .NET MAUI native app (Android / TV / Windows). |
| `Animarr.Shared` | DTOs, enums and API route constants shared by client and server. |

## License

Apache‑2.0
