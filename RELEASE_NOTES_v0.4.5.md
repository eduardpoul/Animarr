# Animarr v0.4.5

A new way to browse episodes: a **detailed list view** alongside the existing grid,
backed by real **per-episode metadata** from TMDB.

## ✨ Features

- **Episode list view — grid or detailed list.** Every series/anime page now has a
  grid⇄list toggle in the season bar. The **detailed list** shows a row per episode with a
  larger still, the episode title, a two-line synopsis, air date, ★ rating and the file's
  resolution (1080p / 720p / …). The grid stays the compact poster layout. A section line
  ("23 of 50 watched · all episodes on disk") sits above the list.
- **Per-account default.** Your preferred layout lives in **Profile → Appearance →
  Episode list view**, and the toggle on a title's page writes the *same* preference — pick
  once and every title opens that way. It now survives a page reload.
- **Real per-episode metadata.** Episode titles, synopses, air dates, ratings and runtimes
  are pulled from TMDB and cached, so the list isn't just "Episode 1, Episode 2…". Data is
  fetched the first time you open a title (a second or two) and **warmed automatically when
  a folder is identified** — no need to re-identify your existing library.
- **English fallback for episode text.** When your metadata language is, say, Russian and
  TMDB has no localized episode titles/synopses for a show, Animarr fills those fields from
  English field-by-field — real titles stay localized wherever translations exist.

## ℹ️ Notes

- Episode metadata needs a **TMDB**-matched title; anime matched only on MyAnimeList have no
  per-episode list and keep the numbered "Episode N" labels.
- A per-episode ★ rating shows only when TMDB actually has votes for that episode.
- The resolution chip is read from the file name, so it appears only when the name carries a
  1080p / 720p / 2160p / 4K tag.
- Already-cached episode data refreshes itself on the next view after upgrading — nothing to
  re-scan.
