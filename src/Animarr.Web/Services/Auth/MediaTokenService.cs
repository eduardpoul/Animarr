using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Animarr.Web.Services.Auth;

/// <summary>What a valid media token says about its bearer.</summary>
/// <param name="Kind">Session, resource-bound link, or DLNA-server link.</param>
/// <param name="UserId">The account the token was issued to (<see cref="Guid.Empty"/>
/// for <see cref="MediaTokenKind.DlnaLink"/>).</param>
/// <param name="ExpiresUtc">Hard expiry; the token is rejected after it.</param>
/// <param name="ResourceHash">For link tokens: the first 16 bytes of SHA-256 over
/// the canonical file path the link was signed for. Null for session tokens.</param>
public sealed record MediaTokenClaims(
    MediaTokenKind Kind, Guid UserId, DateTimeOffset ExpiresUtc, byte[]? ResourceHash);

public enum MediaTokenKind : byte
{
    /// <summary>Per-user, per-session token for clients that can't send the auth
    /// cookie on media requests (the MAUI WebView's cross-origin &lt;img&gt; /
    /// &lt;video&gt;, the TV's native image loader + ExoPlayer, the app's
    /// SignalR connections). Valid for any byte endpoint the user may read.</summary>
    Session = 1,

    /// <summary>Signed link for ONE file, issued to a user and handed to a
    /// third-party device that will never see a cookie (a DLNA renderer the
    /// user casts to, VLC opening an .m3u, mpv).</summary>
    Link = 2,

    /// <summary>Signed link for ONE file emitted by the DLNA MediaServer's
    /// ContentDirectory. Not tied to an account — honoured only while the admin
    /// has the DLNA server switched on, and only from the LAN.</summary>
    DlnaLink = 3,
}

/// <summary>
/// Issues and validates the short, URL-safe signed tokens that authorise the
/// media byte endpoints for clients without the auth cookie.
///
/// Format (base64url, no padding):
/// <code>
///   [0]      version (1)
///   [1]      kind    (MediaTokenKind)
///   [2..18)  user id (Guid, 16 bytes)
///   [18..22) expiry  (uint32 big-endian, unix seconds)
///   [22..38) resource hash (link kinds only — SHA-256(canonical path)[..16])
///   [..+16)  MAC = HMAC-SHA256(key, "animarr.media-token.v1" ‖ payload)[..16]
/// </code>
/// A session token is 51 characters, a link token 72.
///
/// The HMAC key is a 32-byte random secret kept with the app's data
/// (<c>media-token.key</c> next to the database / <c>dp-keys</c>), created on
/// first start. Deleting the file revokes every outstanding token.
///
/// Session tokens are BUCKETED: every call inside the same 6-hour window
/// returns the identical token (expiry = window start + 12 h). Image URLs that
/// carry the token therefore stay byte-identical across page loads and app
/// restarts, so the WebView / image caches keep working, while a token is never
/// valid for more than 12 hours.
/// </summary>
public sealed class MediaTokenService
{
    public const string QueryParam = "mt";
    public const string HeaderName = "X-Media-Token";

    /// <summary>Session-token bucket; a token is valid for two buckets.</summary>
    public static readonly TimeSpan SessionBucket = TimeSpan.FromHours(6);

    private const byte Version = 1;
    private const int UserOffset = 2, ExpOffset = 18, ResOffset = 22;
    private const int SessionPayloadLen = 22, LinkPayloadLen = 38, MacLen = 16;
    private static readonly byte[] MacContext = Encoding.ASCII.GetBytes("animarr.media-token.v1");

    private readonly byte[] _key;
    private readonly TimeProvider _time;

    public MediaTokenService(IConfiguration cfg, ILogger<MediaTokenService> logger, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _key  = LoadOrCreateKey(DataDir(cfg), logger);
    }

    /// <summary>For tests / tools: a service over an explicit key.</summary>
    public MediaTokenService(byte[] key, TimeProvider? time = null)
    {
        if (key.Length < 32) throw new ArgumentException("Key must be at least 32 bytes.", nameof(key));
        _key  = key;
        _time = time ?? TimeProvider.System;
    }

    // ─── Issue ───────────────────────────────────────────────────────────

    /// <summary>The session token for <paramref name="userId"/> in the current
    /// 6-hour bucket (identical for every call inside the bucket).</summary>
    public string IssueSession(Guid userId, out DateTimeOffset expiresUtc)
    {
        var now    = _time.GetUtcNow().ToUnixTimeSeconds();
        var bucket = (long)SessionBucket.TotalSeconds;
        var exp    = now - now % bucket + 2 * bucket;
        expiresUtc = DateTimeOffset.FromUnixTimeSeconds(exp);
        return Encode(MediaTokenKind.Session, userId, exp, null);
    }

    /// <summary>A link token for ONE canonical file path, issued to a user.</summary>
    public string IssueLink(Guid userId, string canonicalPath, TimeSpan lifetime)
        => Encode(MediaTokenKind.Link, userId,
            _time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds(), ResourceHash(canonicalPath));

    /// <summary>A link token for the DLNA ContentDirectory (no account).</summary>
    public string IssueDlnaLink(string canonicalPath, TimeSpan lifetime)
        => Encode(MediaTokenKind.DlnaLink, Guid.Empty,
            _time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds(), ResourceHash(canonicalPath));

    /// <summary>Append <c>mt=&lt;token&gt;</c> to a URL that may already carry a query.</summary>
    public static string AppendToUrl(string url, string token)
        => url + (url.Contains('?') ? '&' : '?') + QueryParam + "=" + Uri.EscapeDataString(token);

    // ─── Validate ────────────────────────────────────────────────────────

    /// <summary>Check MAC, version and expiry. Constant-time MAC compare.</summary>
    public bool TryValidate(string? token, out MediaTokenClaims? claims)
    {
        claims = null;
        if (string.IsNullOrEmpty(token) || token.Length > 128) return false;

        byte[] raw;
        try { raw = WebEncoders.Base64UrlDecode(token); }
        catch { return false; }

        if (raw.Length < SessionPayloadLen + MacLen || raw[0] != Version) return false;
        var kind = (MediaTokenKind)raw[1];
        var payloadLen = kind switch
        {
            MediaTokenKind.Session => SessionPayloadLen,
            MediaTokenKind.Link or MediaTokenKind.DlnaLink => LinkPayloadLen,
            _ => -1,
        };
        if (payloadLen < 0 || raw.Length != payloadLen + MacLen) return false;

        var payload = raw.AsSpan(0, payloadLen);
        Span<byte> mac = stackalloc byte[32];
        ComputeMac(payload, mac);
        if (!CryptographicOperations.FixedTimeEquals(mac[..MacLen], raw.AsSpan(payloadLen, MacLen)))
            return false;

        var exp = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(ExpOffset, 4));
        if (_time.GetUtcNow().ToUnixTimeSeconds() >= exp) return false;

        var userId = new Guid(payload.Slice(UserOffset, 16));
        if (kind == MediaTokenKind.Session && userId == Guid.Empty) return false;
        if (kind == MediaTokenKind.Link && userId == Guid.Empty) return false;
        if (kind == MediaTokenKind.DlnaLink && userId != Guid.Empty) return false;

        claims = new MediaTokenClaims(kind, userId, DateTimeOffset.FromUnixTimeSeconds(exp),
            kind == MediaTokenKind.Session ? null : payload.Slice(ResOffset, 16).ToArray());
        return true;
    }

    /// <summary>SHA-256(canonical path)[..16] — what a link token is bound to.</summary>
    public static byte[] ResourceHash(string canonicalPath)
        => SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)).AsSpan(0, 16).ToArray();

    // ─── Internals ───────────────────────────────────────────────────────

    private string Encode(MediaTokenKind kind, Guid userId, long expUnix, byte[]? resHash)
    {
        var payloadLen = resHash is null ? SessionPayloadLen : LinkPayloadLen;
        var buf = new byte[payloadLen + MacLen];
        buf[0] = Version;
        buf[1] = (byte)kind;
        userId.TryWriteBytes(buf.AsSpan(UserOffset, 16));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(ExpOffset, 4), (uint)Math.Clamp(expUnix, 0, uint.MaxValue));
        resHash?.AsSpan(0, 16).CopyTo(buf.AsSpan(ResOffset, 16));

        Span<byte> mac = stackalloc byte[32];
        ComputeMac(buf.AsSpan(0, payloadLen), mac);
        mac[..MacLen].CopyTo(buf.AsSpan(payloadLen, MacLen));
        return WebEncoders.Base64UrlEncode(buf);
    }

    private void ComputeMac(ReadOnlySpan<byte> payload, Span<byte> dest)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _key);
        h.AppendData(MacContext);
        h.AppendData(payload);
        h.GetHashAndReset(dest);
    }

    /// <summary>Same derivation as Program.cs / MediaCachePaths: the directory
    /// holding the SQLite database (the Docker data volume).</summary>
    private static string DataDir(IConfiguration cfg)
    {
        var conn   = cfg.GetConnectionString("DefaultConnection") ?? "Data Source=Animarr.db";
        var dbPath = conn.Replace("Data Source=", "").Trim();
        var dir    = Path.GetDirectoryName(dbPath);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(dir) ? "." : dir);
    }

    private static byte[] LoadOrCreateKey(string dataDir, ILogger logger)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "media-token.key");
        try
        {
            if (File.Exists(path))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (existing.Length >= 32) return existing;
                logger.LogWarning("media-token.key is too short — generating a new one");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "media-token.key unreadable — generating a new one");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best effort — the data volume is already private to the container */ }
        }
        return key;
    }
}
