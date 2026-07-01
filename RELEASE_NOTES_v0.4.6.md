# Animarr v0.4.6

Skip intro/credits detection gets much better at **long-running shows** and the
**ongoing series** you keep adding episodes to.

## 🎯 Long shows now covered end to end

- **Openings/endings are matched against neighbouring episodes**, not the first
  few of the season. Long donghua (100-300+ episodes) change their opening and
  ending theme every cour, so the old "compare against episodes 1-3" only tagged
  the first block — late episodes got nothing at all. Now each episode is compared
  with its numbered neighbours, which share its theme. In testing this took
  Perfect World from 36 tagged intros to **116** (all of episodes 160-275) and
  Renegade Immortal from a handful to **all 147**.

## 🆕 New episodes get detected automatically

- **When a torrent finishes downloading**, the show's newly-arrived episodes are
  fingerprinted automatically — no need to open them first. A completed torrent is
  the exact "files are fully on disk" moment, so detection always runs on complete
  files (never a half-downloaded one).
- **Manually-added episodes** (not via torrent) are still covered: detection runs
  in the background the first time you open the episode, ready on the next open.

## 🩹 Also

- **AniSkip no longer hangs** on a network that can't reach `api.aniskip.com` —
  it's probed once, and if unreachable, detection falls straight through to audio
  fingerprinting instead of stalling for minutes per episode.

## ℹ️ Notes

- Detection for a long show runs in the background and can take a few minutes per
  title; already-tagged episodes are skipped, so re-scans stay cheap.
