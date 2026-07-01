# Animarr v0.4.7

A small player fix around marking episodes watched.

## 🩹 Fixes

- **Advancing to the next episode now marks the one you're leaving watched**,
  even if playback hadn't crossed the 90%-of-runtime threshold yet. This covers
  the "Play next" button on the end-of-episode card, the HUD Next button, the
  media-key next-track control, and the credits auto-advance — any of them
  moving you forward is treated as "done with this episode," so it no longer
  shows as in-progress afterwards.
