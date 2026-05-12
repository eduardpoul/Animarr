# Animarr

**Animarr** is a self-hosted web app for organizing anime, donghua, films and series. It watches your media folders, identifies content via TMDB / MAL / IMDb, renames files according to configurable patterns, and includes a built-in BitTorrent client.

## ⚡ AI is almost mandatory

Animarr is built around an LLM as the **primary identification engine**, not as an optional add-on. The LLM extracts a canonical title and year out of messy folder/filename strings (pinyin, romaji, Cyrillic, scene release names) **before** the TMDB/MAL/IMDb search runs. Without it, identification of anything non‑English (most Chinese donghua, untranslated Japanese anime, transliterated Russian dubs) tends to fall apart — the title search hits noise instead of the right work.

You can technically run Animarr without an LLM, and it will still match cleanly named English shows like `Breaking Bad (2008)`. For everything else, set up an LLM endpoint — it takes ~5 minutes and is by far the highest‑leverage configuration step. **Minimum recommended model:** `qwen2.5:1.5b` (1 GB RAM, runs comfortably CPU‑only). `qwen2.5:0.5b` works in a pinch but is too small to reliably handle CJK titles. See [AI / LLM setup](#ai--llm-setup) below.

## Features

- **AI-driven identification** — LLM normalises the title, then cross-validates results across **TMDB**, **MAL** and **IMDb** in parallel. Confidence below the auto-apply threshold drops the entry into a **NeedsReview** banner with poster thumbnails + "Open source" links so you can pick the right match in one click.
- **Folder monitoring** — watches one or more directories and auto-renames new files as they arrive, including subtitle pairing.
- **Pattern engine** — regex-based naming rules with named capture groups (`season`, `episode`, `title`); global patterns plus per-folder overrides and exclusions.
- **Media catalog** — poster grid with fanart backdrop slideshow, detail page with seasons, episodes, tags, and ratings.
- **Section folders** — point Animarr at a root directory and it auto-imports each subdirectory as a separately-watched media folder. Flat sections (one video file per movie) are supported too.
- **Torrent client** — built on MonoTorrent; add by magnet link or `.torrent` file, per-file priority, per-torrent speed limits (Mbps). Destination folder picker shows your **identified library titles** rather than raw on-disk names.
- **Safe by default** — Animarr **never** renames folders on disk based on identification. All identification work updates DB associations only; your library tree stays exactly as you laid it out.
- **Ignore rules** — glob masks (`*.nfo`, `fanart*`, …) that skip files from renaming; global or per-folder.
- **Rename history** — full log with one-click revert per file.
- **Recovery tools** — "Restore deleted" rebuilds the catalog after accidental cleanup; orphan FolderWatcher records (whose disk path is gone) are pruned automatically on every Rescan.
- **Multi-language UI** — English and Russian, switchable in Settings.
- **Persistent state** — SQLite database + MonoTorrent fastresume + image cache (lives in `/app/data/image-cache`, **never inside your media tree**) survive container restarts.

## Quick start

### 1. Copy `docker-compose.yml`

```yaml
services:
  animarr:
    image: ghcr.io/eduardpoul/animarr:latest
    container_name: animarr
    restart: unless-stopped
    ports:
      - "8450:8080"
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

Or copy the `docker-compose.yml` from the root of this repo and adjust it.

### 2. Start

```bash
docker compose up -d
```

### 3. Set up an LLM (highly recommended — see [below](#ai--llm-setup))

### 4. Open the UI

```
http://localhost:8450
```

## Volume explanation

| Mount | Purpose |
|-------|---------|
| `animarr-data:/app/data` | SQLite database, MonoTorrent fastresume cache, image cache, DataProtection keys. **Required for persistence.** Image cache lives here too — never inside your media folders. |
| `/your/media/path:/media:rw` | Your media library. Add as many bind mounts as you need — use the container-side path when configuring a folder in Animarr. |

## Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `TZ` | `UTC` | Timezone for log timestamps. E.g. `Europe/Moscow`, `America/New_York` |
| `AppSettings__WatcherDelayMs` | `2000` | Milliseconds the watcher waits after a file appears before processing it |

## AI / LLM setup

Animarr connects to **any OpenAI-compatible chat-completions endpoint**. The provider, model and base URL are configured in **Settings → Metadata → AI / LLM** at runtime — no restart needed.

Supported providers (anything with a `/v1/chat/completions` endpoint):

| Provider | Notes |
|---|---|
| **Ollama** (local) | Free, runs on your hardware, no API key needed. **Recommended for self-hosting.** |
| **OpenAI** | Requires API key from platform.openai.com |
| **Groq** | Fast cloud inference, free tier available |
| **LM Studio** | Local GUI app, OpenAI-compatible server |
| **Together AI**, **Perplexity**, … | Any OpenAI-compatible endpoint |

### Recommended setup: Ollama on the same machine

```bash
# Start Ollama via the included docker-compose (deploy/ollama/)
cd deploy/ollama
docker compose up -d

# Pull the recommended minimum model
docker exec -it ollama ollama pull qwen2.5:1.5b
```

Then in **Settings → Metadata → AI / LLM**:

| Field | Value |
|---|---|
| Provider | OpenAI-compatible URL |
| Base URL | `http://localhost:11434` (or your Ollama server's IP) |
| Model | `qwen2.5:1.5b` |
| API Key | *(leave empty)* |

### Model picker

| Model | RAM | CPU speed | Identification quality |
|---|---|---|---|
| `qwen2.5:0.5b` | ~400 MB | Fast (~8 tok/s on Ryzen 5) | **Fallback only.** Misses many CJK/transliterated titles, sometimes hallucinates years. Use only if RAM is severely constrained. |
| **`qwen2.5:1.5b`** | ~1 GB | Moderate (~3 tok/s on CPU) | **Recommended minimum.** Reliably handles pinyin/romaji, returns clean JSON with `english_title` for CJK inputs, year extraction is solid. |
| `qwen2.5:3b` | ~2 GB | Slow on CPU, OK on GPU | Better edge-case handling for ambiguous franchise names. |
| `gemma3:4b` | ~3 GB | Slow on CPU, fast on GPU | Highest quality on this list, best for messy untagged inputs. |

`qwen2.5:1.5b` is the sweet spot — it understands Japanese anime / Chinese donghua / Russian transliterations, returns the canonical English title alongside, and runs without a GPU on any modern x86/ARM CPU.

### What the LLM actually does

1. **Normalises titles**: `Doupo Gangqiong` → title `Doupo Cangqiong`, english_title `Battle Through the Heavens`. Search runs against the English form when present.
2. **Extracts year and type** from filename / folder path. Year-anchored candidate filter prevents picking a 1968 film when the file clearly says 2025.
3. **Picks the best match** from candidate lists when multiple plausible results come back from TMDB/MAL/IMDb (e.g. "Renegade Immortal" 2023 series vs "Renegade Immortal: Battle of Gods" 2025 movie).
4. **Maps loose files to episodes** when filenames don't match any regex pattern.

When LLM is disabled or unreachable, Animarr falls back to pure regex parsing of folder/file names — works fine for `Show.Name.S01E02.1080p.mkv`, breaks on anything non-Latin.

## Pattern engine

Patterns are regular expressions with named capture groups. Animarr uses them to extract season/episode numbers and build the new filename.

Useful named groups:

| Group | Meaning |
|-------|---------|
| `season` | Season number (optional) |
| `episode` | Episode number |
| `title` | Override show title extracted from the filename |

Example pattern matching `[Group] Show Name - 12 [1080p].mkv`:
```
\[.+?\]\s*(?P<title>.+?)\s*-\s*(?P<episode>\d+)
```

Patterns have a **priority** (lower = checked first) and a **scope**:
- **Global** — applies to all folders of the matching type
- **Folder override** — applies only to one specific folder; can also be set to *exclude* (suppress the global match for that folder)

### Bare-number filenames

Files named with a plain number (e.g. `1.mp4`, `12.mkv`) are automatically recognized as episode files even when no pattern matches. The number is used directly as the episode number and the file is renamed to the standard format (`01.mp4`, `S01E12.mkv` if a season folder is detected).

## Explorer

The **Explorer** page provides a folder-by-folder view of your library.

- Section header has **Rescan**, **Restore deleted** (recovers accidentally-dismissed folders), **Identify**, **Edit**, **Delete** buttons.
- Click any folder row to expand an inline file scan panel.
- Files are displayed as a **tree**: season subdirectories are shown as collapsible nodes; files inside each subdirectory are listed within their folder.
- Select individual files or use the header checkbox to bulk-select; apply renames with one click.
- Delete dialog has a **"Also delete files from disk"** checkbox (off by default).

## Torrent client

- Add by magnet link or `.torrent` file — the add panel has a **File / Magnet** toggle at the top (default: File).
- Destination folder picker shows your **identified library titles** ("Bleach", "Cowboy Bebop") rather than raw on-disk folder names. Manually-edited labels are honoured over auto-identified titles.
- Per-file priority: Normal / High / Low / Skip.
- **Create subfolder** (`+` button) — instantly create a new subdirectory inside a destination folder without leaving the add panel.
- **Flatten subfolders** — after download completes, all files from nested subdirectories are moved to the destination root.
- **Rename / strip root folder** — when a torrent contains a top-level folder, you can rename it or strip it entirely before files land in the destination.
- Speed limits in **Mbps**, globally in Settings and per-torrent in the details panel.
- Auto-rename on completion — when a torrent finishes, the destination folder is scanned and renamed according to the folder's pattern (file-level, never folder-level).

## Ignore rules

Glob masks that tell Animarr to skip certain filenames during renaming. Supports `*` and `?` wildcards. Common examples: `*.nfo`, `*.txt`, `fanart*`, `poster*`.

Rules can be **global** (apply everywhere) or scoped to a specific folder. Managed in **Settings → Ignore Rules**.

## Building from source

```bash
git clone https://github.com/eduardpoul/Animarr
cd Animarr
docker build -t animarr:latest .
docker compose up -d
```

Or with .NET 10 SDK installed:

```bash
cd src/Animarr.Web
dotnet run
```

## License

Apache-2.0
