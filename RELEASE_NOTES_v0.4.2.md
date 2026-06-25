# Animarr v0.4.2

A player release: episodes now **skip their own intros and credits**, and the
in-browser player gets a **purpose-built mobile layout**.

## ⏭️ Skip intro & credits

- **Automatic segment detection** via a four-stage cascade — **AniSkip**
  (community timestamps) → **embedded chapters** → **audio fingerprint**
  (Chromaprint) → **black-frame** scan — so most episodes get accurate
  intro / credits ranges with no manual work.
- **AniSkip with no API key** — the MAL id the lookup needs is resolved through
  AniList, so community timestamps work out of the box.
- **Skip intro** button appears only while the opening plays; one tap seeks past it.
- **Up Next at the credits** — a card fades in at the detected credits start (or
  95% as a fallback) with **Play next**, a **Skip credits** jump and a dismiss ×,
  and auto-advances to the next episode at the end.
- **Adaptive fingerprint alignment** — a cheap ±30s search by default, escalated
  to ±300s only for openings that drift minutes in (e.g. *Bleach: TYBW*), with
  per-run fingerprint caching so the wider pass costs no extra ffmpeg decode.
- **Scan controls** — a segment-scan **progress indicator + rescan button** in
  Settings, plus adaptive background-scanner pacing.

## 📱 Mobile player

- **Touch HUD** — on phones the controls collapse to a big **centre play / pause**,
  **double-tap the left / right edge to skip ±10s**, a full-width scrubber, and a
  **settings gear** that tucks the secondary controls (quality, audio, subtitles,
  aspect, audio-sync, cast, info) into one worded menu.
- **Prev / next auto-hide** when there's no adjacent episode on disk (movies,
  first / last episode).
- Desktop and Android-TV layouts are unchanged.

## 🩹 Fixes

- **Legacy codecs** (MPEG-4 ASP / AVI, etc.) are now re-encoded instead of being
  stream-copied into an fMP4 that wouldn't play.
- **Orphaned folder rows** left by deleted directories are auto-removed on scan
  and at startup.
- The player **Info** panel reports the actual playback output, not the source
  metadata.
