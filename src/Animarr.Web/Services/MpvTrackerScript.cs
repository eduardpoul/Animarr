namespace Animarr.Web.Services;

/// <summary>
/// Generates the mpv lua tracker script with the running Animarr instance's
/// URL baked in. Mirrors what Jellyfin Media Player ships as its `jellyfin
/// shim` — a small lua file the user drops once into <c>mpv/scripts/</c>
/// that polls <c>time-pos</c> and POSTs to a known media-server endpoint.
///
/// Why a generator instead of a static file: the Animarr URL has to be
/// hardcoded into the script (mpv can't easily read external config from
/// a relative file). Different deployments serve their own — install via
/// <c>curl http://animarr/api/mpv-tracker.lua &gt; ~/.config/mpv/scripts/...</c>
/// and the script automatically targets the server it came from.
/// </summary>
public static class MpvTrackerScript
{
    public static string Build(string animarrUrl)
    {
        // Strip trailing slash so concat with /api/... works.
        var url = animarrUrl.TrimEnd('/');
        return $$"""
        -- Animarr mpv tracker
        --
        -- POSTs playback progress to {{url}}/api/watch/external-progress every
        -- ~5 seconds while a file is playing, plus on end-of-file. The Animarr
        -- server uses these pings to keep WatchState in sync — Continue, %
        -- watched, and "watched" auto-flip work exactly the same as the
        -- in-browser HLS player.
        --
        -- Install:
        --   curl {{url}}/api/mpv-tracker.lua \
        --     > ~/.config/mpv/scripts/animarr-tracker.lua   # Linux/macOS
        --   Or for mpv.net on Windows:
        --     %APPDATA%\mpv.net\scripts\animarr-tracker.lua
        --
        -- The script only fires for URLs served by this Animarr instance
        -- ({{url}}); other files mpv plays are ignored.

        local mp    = require 'mp'
        local utils = require 'mp.utils'
        local msg   = require 'mp.msg'

        local ANIMARR_URL = "{{url}}"
        local POST_ENDPOINT = ANIMARR_URL .. "/api/watch/external-progress"
        local POLL_INTERVAL = 5  -- seconds between progress POSTs

        local current_path = nil   -- canonical /Poul-D1/... path on the Animarr server
        local last_post_pos = -1   -- seconds; we skip POSTs when position hasn't moved

        -- URL-decode (mpv URLs come encoded for cyrillic / spaces).
        local function url_decode(s)
            return (s:gsub("%%(%x%x)", function(h) return string.char(tonumber(h, 16)) end)
                     :gsub("+", " "))
        end

        -- Pull the `path` query parameter out of an Animarr URL like
        -- http://host:8080/api/file?path=%2FPoul-D1%2F...
        local function extract_animarr_path(url)
            if not url or url == "" then return nil end
            if not url:find(ANIMARR_URL, 1, true) then return nil end
            local q = url:match("[?&]path=([^&]+)")
            if not q then return nil end
            return url_decode(q)
        end

        -- POST progress to Animarr. We shell out to curl since lua doesn't
        -- have HTTP built in, and mpv ships subprocess support cross-platform.
        local function post_progress(path, pos, dur, delta)
            if not path then return end
            local body = string.format(
                '{"path":%q,"positionSec":%s,"durationSec":%s,"playedDeltaSec":%d}',
                path,
                tostring(pos or 0),
                dur and tostring(dur) or "null",
                delta or 0)
            local res = utils.subprocess({
                args = { "curl", "-s", "-X", "POST",
                         "-H", "Content-Type: application/json",
                         "--max-time", "3",
                         "-d", body,
                         POST_ENDPOINT },
                cancellable = false,
            })
            if res.status ~= 0 then
                msg.warn("animarr: POST failed (curl exit " .. tostring(res.status) .. ")")
            end
        end

        mp.register_event("file-loaded", function()
            local raw = mp.get_property("path")
            current_path = extract_animarr_path(raw)
            if current_path then
                msg.info("animarr: tracking " .. current_path)
                last_post_pos = -1
            else
                msg.verbose("animarr: not an Animarr URL, skip tracking")
            end
        end)

        mp.add_periodic_timer(POLL_INTERVAL, function()
            if not current_path then return end
            local pos = mp.get_property_number("time-pos", 0)
            local dur = mp.get_property_number("duration", 0)
            if pos < 1 then return end
            -- Don't spam when paused on the same frame.
            if math.abs(pos - last_post_pos) < 1 then return end
            local delta = last_post_pos > 0 and math.min(POLL_INTERVAL + 1, math.max(0, pos - last_post_pos)) or POLL_INTERVAL
            last_post_pos = pos
            post_progress(current_path, pos, dur > 0 and dur or nil, math.floor(delta))
        end)

        -- Capture final position on close / next file. `end-file` fires for
        -- all of: EOF, user quit, stop, next playlist item.
        mp.register_event("end-file", function(evt)
            if not current_path then return end
            local pos = mp.get_property_number("time-pos", 0)
            local dur = mp.get_property_number("duration", 0)
            if pos > 0 then
                post_progress(current_path, pos, dur > 0 and dur or nil, 0)
            end
            current_path = nil
            last_post_pos = -1
        end)

        msg.info("animarr-tracker loaded — server " .. ANIMARR_URL)
        """;
    }
}
