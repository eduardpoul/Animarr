using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;

namespace Animarr.Web.Endpoints;

/// <summary>
/// "Send to TV" — DLNA renderer discovery + control. The DLNA media server
/// (SSDP NOTIFY / ContentDirectory) endpoints live in Program.cs; this
/// wraps the cast (control-point) side only.
///
/// Signed-in only (<see cref="MediaAuth.Policies.MediaSession"/>): the web
/// player sends its cookie, the MAUI WebView player (behind the cookie-less
/// loopback proxy) appends its session media token. The play path is checked
/// by <see cref="MediaPathValidator"/> (library roots + the caller's role
/// folders), and the URL handed to the renderer — which can never sign in —
/// carries a link token signed for that one file and that user.
///
/// What the renderer gets (plan 6.6): the raw file (<c>/api/file</c>) when it
/// takes the container and the default audio is wanted; otherwise an MPEG-TS
/// remux (<c>/api/dlna/stream</c>) with the chosen audio track / external dub
/// (AVTransport has no track selection), shifted by its sync offset, or a
/// re-encoded video. A chosen text
/// subtitle goes along as an SRT URL in the item metadata, and the start
/// position as a Seek once the renderer plays.
/// </summary>
internal static class DlnaCastEndpoints
{
    /// <summary>How long a cast URL stays playable: a long film plus a pause.
    /// TVs re-request with Range on every seek, so it must outlive the session.</summary>
    private static readonly TimeSpan CastLinkLifetime = TimeSpan.FromHours(8);

    public static IEndpointRouteBuilder MapDlnaCastEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.DlnaRenderers, async (
            DlnaCastService cast,
            CancellationToken ct) =>
        {
            var renderers = await cast.RefreshAsync(ct);
            var dtos = renderers
                .Select(r => new DlnaRendererDto(
                    Udn:           r.Udn,
                    FriendlyName:  r.FriendlyName,
                    ModelName:     r.ModelName ?? string.Empty,
                    Manufacturer:  r.Manufacturer ?? string.Empty,
                    ControlUrl:    r.AvTransportControlUrl,
                    LastSeenUtc:   r.LastSeen))
                .ToArray();
            return Results.Ok(dtos);
        }).RequireAuthorization(MediaAuth.Policies.MediaSession);

        app.MapPost(ApiRoutes.DlnaPlay, async (
            DlnaPlayRequest request,
            DlnaCastService cast,
            DlnaService dlna,
            MediaPathValidator pathValidator,
            MediaTokenService tokens,
            IUserContext userCtx,
            TrackPreferenceService trackPrefs,
            ExternalTrackService externalTracks,
            SubtitleTrackInfoService subtitleTracks,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RendererUdn))
                return Results.BadRequest("RendererUdn is required.");

            var (ok, fullPath, early) = await pathValidator.ResolveLibraryFileAsync(request.FilePath, ct);
            if (!ok) return early!;
            if (userCtx.CurrentUserId is not Guid uid) return Results.Unauthorized();

            // ── What to play: explicit picks, else the profile's defaults ──────
            int audioIndex = Math.Max(0, request.AudioTrackIndex ?? 0);
            string? externalAudio = string.IsNullOrWhiteSpace(request.ExternalAudio) ? null : request.ExternalAudio;
            int? subtitleTrack = request.SubtitleTrack;
            string? subtitlePath = string.IsNullOrWhiteSpace(request.SubtitlePath) ? null : request.SubtitlePath;
            bool audioGiven = request.AudioTrackIndex is not null || externalAudio is not null;
            bool subtitleGiven = subtitleTrack is not null || subtitlePath is not null;
            // The audio's sync offset: asked for, else the defaults' one (with
            // the audio they picked), else a dub's DELAY (below).
            int? audioOffsetMs = request.AudioOffsetMs;
            if (request.UsePrefs == true && (!audioGiven || !subtitleGiven)
                && await trackPrefs.TryResolveAsync(uid, fullPath!, ct) is { } defaults)
            {
                if (!audioGiven && defaults.Audio is { } a)
                {
                    if (a.Kind == TrackPreferenceService.KindExternal) externalAudio = a.Path;
                    else audioIndex = a.Index ?? 0;
                    audioOffsetMs ??= defaults.AudioOffsetMs;
                }
                if (!subtitleGiven && defaults.Subtitle is { } s)
                {
                    if (s.Kind == TrackPreferenceService.KindEmbedded) subtitleTrack = s.Index;
                    else if (s.Kind == TrackPreferenceService.KindExternal) subtitlePath = s.Path;
                }
            }

            // External files must be this video's own (discovered) tracks.
            var external = externalAudio is not null || subtitlePath is not null
                ? await externalTracks.FindForVideoAsync(fullPath!, probe: false, ct)
                : [];
            if (externalAudio is not null)
            {
                var dub = external.FirstOrDefault(t => t.Kind == "audio" && SamePath(t.Path, externalAudio));
                if (dub is null) return Results.BadRequest("ExternalAudio is not an external audio track of this file.");
                externalAudio = dub.Path;
                audioOffsetMs ??= dub.DelayMs;
            }
            int offsetMs = Math.Clamp(audioOffsetMs ?? 0, -TrackPreferenceService.MaxAudioOffsetMs, TrackPreferenceService.MaxAudioOffsetMs);

            // ── Subtitles: an SRT URL — text tracks only ──────────────────────
            var baseUrl = dlna.AdvertisedHost ?? "http://localhost:8080";
            var link = tokens.IssueLink(uid, fullPath!, CastLinkLifetime);
            string? subtitleUrl = null;
            if (subtitlePath is not null)
            {
                var sub = external.FirstOrDefault(t => t.Kind == "subtitle" && SamePath(t.Path, subtitlePath));
                if (sub is null) return Results.BadRequest("SubtitlePath is not an external subtitle of this file.");
                if (sub.Image != true)
                    subtitleUrl = MediaTokenService.AppendToUrl(
                        $"{baseUrl}/api/subtitle/srt?path={Uri.EscapeDataString(sub.Path)}&track=0",
                        tokens.IssueLink(uid, sub.Path, CastLinkLifetime));
            }
            else if (subtitleTrack is int st && st >= 0
                     && await subtitleTracks.CodecOfAsync(fullPath!, st, ct) is { } codec && !SubtitleCodecs.IsImage(codec))
            {
                subtitleUrl = MediaTokenService.AppendToUrl(
                    $"{baseUrl}/api/subtitle/srt?path={Uri.EscapeDataString(fullPath!)}&track={st}", link);
            }

            // ── Raw file or remux ─────────────────────────────────────────────
            // Build the stream URL from the SAME origin the DLNA description
            // advertises. Using the inbound Request.Host header would break
            // when the user opens Animarr at https://hostname:8443 but the TV
            // (on the LAN) can't resolve that hostname — and would also point
            // at port 8443 (TLS), which TVs reject because of our self-signed
            // cert. The SSDP LOCATION header reaches all TVs at this same
            // origin already, so we know they can hit it.
            var ext  = Path.GetExtension(fullPath!).ToLowerInvariant();
            var mime = MediaMime.ForVideoExtension(ext);
            bool remux = audioIndex > 0 || externalAudio is not null || offsetMs != 0 || request.Transcode == true;
            if (!remux && await cast.GetSinkProtocolsAsync(request.RendererUdn, ct) is { } sink
                && !DlnaCastService.SinkAccepts(sink, mime))
                remux = true;   // the renderer says it doesn't take this container

            string streamUrl;
            if (remux)
            {
                var q = $"path={Uri.EscapeDataString(fullPath!)}&audio={audioIndex}";
                if (externalAudio is not null) q += "&external=" + Uri.EscapeDataString(externalAudio);
                if (request.Transcode == true) q += "&transcode=1";
                if (offsetMs != 0) q += "&offset=" + offsetMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                streamUrl = MediaTokenService.AppendToUrl($"{baseUrl}/api/dlna/stream?{q}", link);
                mime = DlnaStreamService.ContentType;
            }
            else
            {
                // /api/file — raw byte-stream with Range support. TVs/AVRs decode
                // the source natively (HEVC + AC3 + Atmos passthrough and 5.1
                // surround intact). Use the *validated* fullPath, not
                // request.FilePath — the URL pointed at the TV must be the same
                // canonical form /api/file will accept. The renderer can't sign
                // in: the URL carries a link token bound to this file + this user
                // (role folders keep applying on fetch).
                streamUrl = MediaTokenService.AppendToUrl(
                    $"{baseUrl}{ApiRoutes.File}?path={Uri.EscapeDataString(fullPath!)}", link);
            }
            var title = Path.GetFileNameWithoutExtension(fullPath!);

            var success = await cast.PlayAsync(request.RendererUdn, streamUrl, title, mime, subtitleUrl,
                remux ? DlnaStreamService.ProtocolFlags : null, request.StartTimeMs, ct);
            http.Response.Headers["X-Animarr-Cast"] = (remux ? "remux" : "direct") + (subtitleUrl is null ? "" : "+subtitles");
            return success ? Results.NoContent() : Results.Problem("Renderer rejected the request.", statusCode: 502);
        }).RequireAuthorization(MediaAuth.Policies.MediaSession);

        return app;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
