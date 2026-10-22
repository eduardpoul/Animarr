namespace Animarr.Shared.Models;

// Per-file playback facts the players read next to /api/probe
// (docs/playback-master-plan-2026-10.md §6.2, Этап 7, 3.3). All cached per
// file version (path + mtime + size) on the server.

/// <summary>One container chapter — <c>GET /api/chapters?path=</c> answers an
/// array of these, in start order (empty when the file has none).</summary>
/// <param name="Index">Position in the list (0-based).</param>
/// <param name="Title">The chapter's title tag ("Opening", "Part A") or null.</param>
/// <param name="StartSec">Start in seconds of the file's timeline.</param>
/// <param name="EndSec">End in seconds; a missing end is the next chapter's
/// start (the file's duration for the last one).</param>
public sealed record ChapterDto(int Index, string? Title, double StartSec, double EndSec);
