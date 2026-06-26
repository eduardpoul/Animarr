# Animarr v0.4.4

A small reliability release for **skip intro/credits** detection, plus quieter logs.

## 🩹 Fixes

- **Skip detection no longer stalls when AniSkip is unreachable.** AniSkip is looked up
  once per episode, so on a network that can't reach `api.aniskip.com` — DNS resolves and
  the TCP port opens, but the TLS handshake gets dropped by a broken-MTU or DPI network
  (the API itself is fine, answering in ~0.3s normally) — a long season could hang for
  minutes per episode on the old 100-second timeout. Animarr now **probes AniSkip once,
  caches the verdict for 10 minutes, and falls straight through to audio-fingerprint
  (chromaprint) detection** when the host doesn't answer — no more per-episode hangs. The
  lookup timeout is also capped at 15 seconds, and a real timeout trips the same skip.
- **Quieter container logs** — Entity Framework no longer logs every SQL statement at
  Information level, so the background queue's 2-second poll stops flooding `docker logs`.

## ℹ️ Notes

- When AniSkip is unreachable from your server, openings/endings are still detected via
  audio fingerprinting; only the crowd-sourced AniSkip timestamps are skipped. Getting
  AniSkip itself to work is network-side (MTU/MSS clamping, or a VPN/proxy for
  `api.aniskip.com`).
