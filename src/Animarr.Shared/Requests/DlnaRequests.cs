namespace Animarr.Shared.Requests;

/// <summary>"Send to TV" — pushes a media file to a discovered DLNA renderer.</summary>
/// <param name="StartTimeMs">Start position: the renderer is told to Seek
/// there once it reports PLAYING (renderers without Seek start at 0).</param>
/// <param name="AudioTrackIndex">a:N to play; anything but 0 means the
/// renderer gets a server remux (MPEG-TS) instead of the raw file — AVTransport
/// has no track selection.</param>
/// <param name="ExternalAudio">An external dub (a path from
/// /api/external-tracks) muxed in instead of the file's own audio (remux).</param>
/// <param name="SubtitleTrack">Embedded text subtitle s:N, handed to the
/// renderer as an SRT URL.</param>
/// <param name="SubtitlePath">External text subtitle file (from
/// /api/external-tracks) instead of <paramref name="SubtitleTrack"/>.</param>
/// <param name="UsePrefs">True: audio / subtitles not given explicitly come
/// from the profile's track defaults (/api/me/track-prefs/resolve).</param>
/// <param name="Transcode">True: re-encode the video to H.264 for a renderer
/// that can't decode the source (software, ≤ 1080p).</param>
public sealed record DlnaPlayRequest(
    string RendererUdn,
    string FilePath,
    long? StartTimeMs,
    int? AudioTrackIndex = null,
    string? ExternalAudio = null,
    int? SubtitleTrack = null,
    string? SubtitlePath = null,
    bool? UsePrefs = null,
    bool? Transcode = null);
