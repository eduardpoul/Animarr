# Animarr v0.4.8

A big housekeeping release: the whole UI is now translatable (and translated
into all five languages), the API is closed by default, audio/subtitle
preferences actually do something, and a lot of the server was tidied up
without changing how it behaves.

## 🔒 Security

- **The API is now closed by default.** Previously only a handful of endpoint
  groups required a login; the rest — including the filesystem browser, the
  app-config store (which holds your TMDB/MAL/LLM API keys), torrents, search
  and more — were reachable with no cookie at all. A default-deny authorization
  policy now requires an authenticated user everywhere, with explicit opt-outs
  only for the genuinely cookie-less surface (login, TV pairing, server-info
  probe, and the media byte streams, which are gated by library-path checks).
- **TV pairing is rate-limited** (per IP) so the 6-digit code can't be
  brute-forced.
- **Fixed an access-scope leak** where a folder-restricted role could still
  change categories on titles outside its allowed folders.
- **Fixed two duplicate route registrations** that made `/api/hardware-info`
  and "Send to TV" (DLNA renderers) return HTTP 500.

## 🌍 Localization

- **The entire interface is now localized.** Whole screens that used to be
  English-only — Settings, the Edit Metadata drawer, Torrents, the Users/Roles
  and Server panels, Login/Setup/Welcome, Discovery, the title page (synopsis,
  details, the Continue button) and more — now render in your language.
- **English, Русский, Українська, Deutsch and Español are at full parity** —
  every string is translated in every language (no more English fall-through).

## 🎚️ Audio & subtitles

- **Preferred audio & subtitle languages now take effect.** The player picks
  the matching subtitle track on open, and — on transcoding sessions — the
  matching audio track. Direct Play is never sacrificed to switch language.
- **Subtitle size** from your preferences is now applied to the player.
- **Removed two settings that never did anything**: "Default volume" (the
  player already remembers volume per device) and "Audio passthrough" (a
  browser can't bitstream-passthrough; on DLNA/Direct Play the original audio
  already passes through untouched).
- The **Audio tab is now "Settings"** and hosts the interface-language picker
  at the top.

## 🩹 Fixes

- Flat-section movie auto-registration no longer picks up files from nested
  subfolders.
- Fixed an `ObjectDisposedException` race in the torrent engine when a torrent
  was removed and re-added quickly.
- HLS seek-restarts are now serialized per session instead of globally, so
  scrubbing in one playback no longer stalls another viewer's stream.
- Episode-metadata fetches log their failures instead of silently leaving
  episodes blank, and no longer do a redundant English re-fetch for `en-US`.

## 🧹 Under the hood

No behaviour change — just a much healthier codebase:

- `Program.cs` shrank from ~1400 to ~420 lines; the image/video/HLS/DLNA/probe
  endpoints moved into focused endpoint files, with the triplicated path
  validation and MIME maps de-duplicated into one place.
- The two 2000-line god-classes (`MetadataService`, `HlsSessionService`) were
  split into partial files by responsibility.
- Shared helpers duplicated between the web and UI projects were moved into the
  shared library.
