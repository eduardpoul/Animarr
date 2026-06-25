# Animarr v0.4.3

A metadata release: catalog metadata can now be fetched in **your language**, and the
mobile player picks up a batch of **touch-handling fixes**.

## 🌐 Metadata language

- **Pick a metadata language** in Settings → Metadata — the same set the interface
  offers (English, Русский, Українська, Deutsch, Español). Titles, overviews and
  genres come from TMDB in that language.
- **One switch re-localizes the whole library** — changing the language starts a
  background pass that re-fetches every identified title, with a **progress bar** right
  under the selector. Titles you add later use the chosen language straight away.
- **Localized posters** — when TMDB has a poster in your language it's preferred;
  otherwise the existing artwork is kept.
- **English fallback per field** — anything without a translation (often the synopsis)
  falls back to English instead of going blank. Anime matched only on MyAnimeList stays
  in its original / romaji form.
- **Genres stay smart** — stored canonically in English so the catalog logic (anime
  detection, Anime / Donghua categories, theme-music matching) keeps working, while the
  UI shows them translated.

## 🩹 Fixes

- **Theme music no longer depends on the metadata language** — a title whose opening /
  ending theme was already found keeps playing it whatever language you pick.
- **Mobile player touch handling** — controls toggle instantly so play / pause is always
  tappable; a lone tap hides the HUD while a double-tap on an edge seeks ±10s; the HUD
  stays put in landscape; and a stray "ghost" mouse-move no longer flashes the overlay.
  The MAUI app also stops mistaking a touch phone for a TV.
