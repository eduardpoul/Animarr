First major release since v0.1.7. ~40 commits of identification accuracy, recovery tooling, and removing destructive defaults.

## ⚡ The headline

Animarr now leads with an **LLM as the primary identification engine** rather than as an optional add-on. Folder names in pinyin, romaji, Cyrillic, or scene-release notation are normalised to canonical English titles **before** the TMDB / MAL / IMDb search runs. Cross-validation between the three sources, year-anchored filtering, and a confidence-gated UI eliminate most of the misidentifications that plagued v0.1.x.

**Recommended minimum LLM:** `qwen2.5:1.5b` via Ollama (1 GB RAM, CPU-only OK). The previous `qwen2.5:0.5b` recommendation has been demoted to fallback — it can't reliably handle CJK titles. See the rewritten [README](README.md) for details.

## 🛡️ Safety: no more destructive auto-renames

Two catastrophic data-corruption incidents in v0.1.x prompted a full audit. As of v0.2.0:

- **Container-folder auto-rename has been removed entirely.** Identification only updates DB associations now. Your on-disk folder structure is **never** modified by Animarr based on a TMDB/MAL match. The setting toggle is gone.
- **Image cache moved out of the user's media tree.** Posters / fanart / logos / episode stills now live in `/app/data/image-cache/`, not in `<your-media>/.animarr/`. Deleting `.animarr/` from your library no longer triggers re-creation on the next scan.
- **Ghost catalog entries don't resurrect after Identify.** Deleting a card from `/catalog` removes the underlying FolderWatcher + dismisses the path. Identify can't bring it back.
- **Orphan FolderWatcher rows are pruned** before every Rescan / Restore / Identify (refuses to touch a section whose root is unreachable — protects against temporary mount blips).

## 🎯 Identification improvements

- **Cross-source validation:** when two of TMDB / MAL / IMDb independently agree on (normalised-title, year), every candidate in that group gets a +0.25 score boost.
- **Type filter:** when the LLM identifies a folder as a series, movie-typed IMDb candidates are dropped before LLM selection (and vice versa). Prevents Renegade Immortal donghua (2023) being matched against the Battle of Gods movie (2025).
- **CJK english_title preference:** if the LLM's primary title is non-ASCII and it provided an `english_title`, search runs against the English form. Massive accuracy boost for Chinese donghua and untranslated Japanese anime.
- **Year-anchored filtering:** candidates whose year doesn't match (±1) the folder year are hidden from the LLM picker. No more 1968 films matched to a 2025 folder.
- **Fuzzy season-folder matching:** local season folders named "Thousand Year Blood War" (Bleach S17 etc.) are matched against TMDB season names via Jaccard word overlap. Episodes inside non-standard layouts get mapped correctly.
- **NeedsReview banner with thumbnails:** low-confidence results don't auto-apply — they land in NeedsReview with a top-3 candidate banner including 60×90 poster thumbnails and an "Open source" link to each candidate's TMDB / MAL / IMDb page so you can pick the right match in one click.
- **Confidence floor:** when the LLM has picked something but the raw score is < 0.50, the score is floored at 0.50 so the result always reaches NeedsReview (never silently Failed).
- **Single-file movie support:** flat sections — one video file per movie inside a single root — are first-class. Each file gets its own MediaItem and identification runs against the filename, not the section's generic dir name.
- **IMDb as a third source:** falls back to imdbapi.dev direct lookup when TMDB doesn't have a title.
- **MAL synthetic Season 1** when only MAL recognises the show — MediaDetail's seasons tab gets populated even without TMDB.

## 🩹 Recovery tools

- **"Restore deleted" button** in section header — wipes the dismissed-paths list, re-discovers children, forces `IdentifyEnabled = true`, drops leftover queue rows, and queues fresh identification (with `ForceRefresh`) for every restored folder. One-click recovery from earlier deletion mishaps.
- **Manual ID panel** accepts TVDB slug-style URLs (`https://thetvdb.com/?tab=series&id=74796` or `/series/74796`) — not just numeric IDs.
- **Manual "Use this"** on a NeedsReview candidate works for IMDb entries now too (previously threw because `ApplyManualAsync(int)` didn't recognise `imdb_search`).
- **Cache-relocation re-identify:** any MediaItem whose stored PosterPath now references a missing file is automatically re-queued on the next Rescan with ForceRefresh, so posters move to the new central cache without manual intervention.

## 🎨 UI / UX

- **Reconnect modal** has a 3-second grace period — brief LAN blips no longer flash the modal at you. Blazor retry tuned to retry every 200ms for the first 3 attempts, then 1s/2s/5s. Most disconnects resolve invisibly.
- **Add-torrent panel redesigned** with a File / Magnet toggle at the top (default: File). Magnet field binding bug fixed (`Value="_addMagnet"` was being interpreted as a string literal). Single-file movie entries are filtered out of the destination folder picker.
- **Torrent destination picker** shows your **identified library titles** ("Bleach") instead of raw on-disk names. Manually-edited labels win over auto-identified titles.
- **Delete folder dialog** has an opt-in "Also delete files from disk" checkbox (off by default). Disk delete is supported for both single files (SingleFilePath) and directories.
- **MediaDetail shows ALL TMDB episodes**, marking only the ones physically present on disk with a ✓ badge. Previously hid episodes whose file wasn't downloaded yet.
- **Section subdirectories visible** inside show folders (e.g. `Season 1`, `Season 2`) — were previously hidden in the Explorer.

## 🐛 Notable bug fixes

- `{searchTitle}` interpolation bug in the IMDb empty-results log (literal braces were escaped).
- Section dialog wasn't setting `IdentifyEnabled` on its auto-discovered children.
- `LooksLikeMediaFolder` heuristic added to skip empty/junk-named subfolders ("New Folder" no longer reappears in the catalog every time you delete it).
- Image refresh logic forced for new MediaItems so the new cache directory gets populated.

## 🔧 Internal

- Removed unused `torrentEngine` / `FolderWatcherService` DI from `MetadataService` after auto-rename was stripped.
- `AppConfigKeys.AutoRenameContainerFolder` marked `[Obsolete]` for grep / cleanup of stale config rows.
- LLM service refactored around `Microsoft.Extensions.AI` for OpenAI-compatible endpoint flexibility.

## 📦 Docker image

```
docker pull ghcr.io/eduardpoul/animarr:v0.2.0
docker pull ghcr.io/eduardpoul/animarr:latest
```
