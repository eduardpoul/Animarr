# Animarr

**Animarr** is a self-hosted web application for organizing anime (and other media) files.  
It watches your media folders, renames files according to configurable patterns, and integrates a built-in torrent client.

## Features

- **Folder monitoring** — watches one or more directories and auto-renames new files as they arrive
- **Pattern engine** — regex-based naming rules with named capture groups (`season`, `episode`); global patterns plus per-folder overrides and exclusions
- **Torrent client** — built on MonoTorrent; add by magnet link or `.torrent` file, per-file priority, per-torrent speed limits (Kbps)
- **Section folders** — point Animarr at a root directory and it auto-imports each subdirectory as a separate monitored folder
- **Ignore rules** — glob masks (e.g. `*.nfo`, `fanart*`) that skip files from renaming; global or per-folder
- **Rename history** — full log with one-click revert per file
- **Multi-language UI** — English and Russian, switchable in Settings
- **Persistent state** — SQLite database + MonoTorrent fastresume survive container restarts

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

### 3. Open the UI

```
http://localhost:8450
```

## Volume explanation

| Mount | Purpose |
|-------|---------|
| `animarr-data:/app/data` | SQLite database, MonoTorrent fastresume cache, cached `.torrent` files. **Required for persistence.** |
| `/your/media/path:/media:rw` | Your media library. Add as many bind mounts as you need — use the container-side path when configuring a folder in Animarr. |

## Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `TZ` | `UTC` | Timezone for log timestamps. E.g. `Europe/Moscow`, `America/New_York` |
| `AppSettings__WatcherDelayMs` | `2000` | Milliseconds the watcher waits after a file appears before processing it |

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

The **Explorer** page provides a folder-by-folder view of your media library.

- Click any folder row to expand an inline file scan panel
- Files are displayed as a **tree**: season subdirectories are shown as collapsible nodes; files inside each subdirectory are listed within their folder
- Click a directory node to collapse/expand it (all nodes are expanded by default after a scan)
- The scan panel shows original names, predicted new names, rename status, and reason for each file
- Select individual files or use the header checkbox to bulk-select; apply renames with one click

## Torrent client

- Add by magnet link or `.torrent` file
- Select which files to download (per-file priority: Normal / High / Low / Skip)
- **Create subfolder** (`+` button) — instantly create a new subdirectory inside a destination folder without leaving the add panel
- **Flatten subfolders** — after download completes, all files from nested subdirectories are moved to the destination root (useful when a torrent wraps everything in an extra folder)
- **Rename / strip root folder** — when a torrent contains a top-level folder, you can rename it or strip it entirely before files land in the destination
- Speed limits are set in **Kbps** (kilobits per second), globally in Settings and per-torrent in the details panel
- Auto-rename on completion — when a torrent finishes, the destination folder is scanned and renamed according to the folder's pattern

## Ignore rules

Glob masks that tell Animarr to skip certain filenames during renaming. Supports `*` and `?` wildcards.

Common examples: `*.nfo`, `*.txt`, `fanart*`, `poster*`, `thumb*`

Rules can be **global** (apply everywhere) or scoped to a specific folder.  
Managed in **Settings → Ignore Rules**.

## Building from source

```bash
git clone https://github.com/eduardpoul/Animarr
cd animarr
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

