# Animarr v0.4.0

A playback-quality release. Video now plays **natively** wherever the browser
can decode it — original bitstream, HDR preserved, no re-encode — plus a
security-critical ffmpeg bump and a fresh typeface.

## 🔒 Security

- **Bundled ffmpeg upgraded to 8.1.2**, patching **CVE-2026-8461** (MagicYUV
  decoder heap out-of-bounds write). The image now ships a pinned,
  checksum-verified static build instead of the distro package.

## ▶️ Native playback — original quality & HDR

- **Direct Play for MP4** (H.264 and HEVC, 8/10-bit + AAC): the file plays on a
  real `<video>` element — instant start, perfect A/V sync, zero transcode.
- **Direct Stream for MKV** (and other non-MP4 containers): remuxed on the fly
  to progressive fMP4 (video stream-copy, audio → AAC). The original video
  bitstream and **HDR pass through unchanged**, and native playback outputs HDR
  more reliably than the MSE/HLS path. Seeking re-requests the remux at the new
  offset, with an instant fast-path when the target is already buffered.
- **HEVC served at original quality** (stream-copy) instead of being
  re-encoded — the blocky look on heavy HEVC files is gone.
- **Quality menu with bitrate presets** (≤6 → 200 Mbps, scaled to the source)
  for when you *do* want to cap the stream; the cap now also correctly applies
  to files that would otherwise play natively.
- **Automatic audio-sync** — the stream-copy path measures the real B-frame
  reorder delay from the packets and offsets audio accordingly, so heavy HEVC
  remuxes stay in sync without manual tweaking.

## ✅ Watch tracking

- **Re-watching shows the resume bar again** — `IsWatched` now clears when
  progress drops below 90%, so a restarted episode no longer looks "finished"
  and masks its progress bar.

## 🎨 Interface

- **New typeface — Hanken Grotesk** across the whole UI (replaces Geist /
  Archivo Black).

## 🩹 Fixes

- `/api/hls/start` returned **400** when the client advertised HEVC support
  (string-vs-bool query binding).
- +1-frame safety bias on the stream-copy audio offset.

---

**On NVIDIA RTX VSR / Video HDR:** these GPU *overlay* features still don't
engage in the browser player — Chrome won't promote our composited `<video>` to
a hardware overlay the way a bare page does. For RTX upscaling/HDR use a native
client or cast. Genuine **HDR10 passthrough** does work on the native Direct
Play / Direct Stream paths.
