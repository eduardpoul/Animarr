using System.Security.Claims;
using System.Security.Cryptography;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QRCoder;

namespace Animarr.Web.Endpoints;

/// <summary>
/// TV pairing flow (v5 Phase 7).
///
/// Goal: let a smart TV / Android TV WebView sign in without typing on the
/// remote. The TV opens <c>/pair</c>, which calls <see cref="ApiRoutes.PairInit"/>
/// to mint a 6-char code (e.g. "4F-7K-92") + QR payload. The TV displays both,
/// then polls <see cref="ApiRoutes.PairPoll"/> every 2s. Meanwhile the user
/// opens Animarr on a phone they're already signed into, scans the QR (or
/// types the code), and the phone POSTs <see cref="ApiRoutes.PairConfirm"/>.
/// The next /pair/poll response carries Set-Cookie for the same identity, so
/// when the TV refreshes / it's authenticated.
///
/// Storage: <see cref="IMemoryCache"/> — codes expire after 5 minutes and the
/// data is tiny (one record per pending pair). Single-server only by design.
/// </summary>
internal static class PairEndpoints
{
    /// <summary>Memory-cache key prefix so pair entries don't collide with
    /// other in-process cache consumers.</summary>
    private const string CacheKeyPrefix = "pair:";

    /// <summary>How long a generated code stays valid. After this the TV needs
    /// to click "Refresh" (or remount) to mint a new one.</summary>
    private static readonly TimeSpan PairTtl = TimeSpan.FromMinutes(5);

    /// <summary>Code alphabet — A-Z + 2-9, with I/O/0/1 omitted so users can't
    /// confuse 1/I/l or 0/O when reading the dash-separated code off a TV.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static IEndpointRouteBuilder MapPairEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── Init: TV asks for a fresh code ──────────────────────────────
        // Anonymous — the TV has no cookie yet. Body carries the device
        // name so the cache entry can show "Living Room TV" in any future
        // audit log / "active sessions" panel.
        app.MapPost(ApiRoutes.PairInit, (
            PairInitRequest req,
            IMemoryCache cache,
            HttpContext http) =>
        {
            var code = GenerateCode();
            // Build the deep-link URL the phone can scan. animarr:// is the
            // MAUI URI scheme; the https:// fallback lives in the dto so the
            // /pair page can render a "or visit X" copy beneath the QR.
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var qrPayload = $"animarr://pair?code={Uri.EscapeDataString(code)}&server={Uri.EscapeDataString(origin)}";

            var entry = new PairRequest(
                Code: code,
                DeviceName: string.IsNullOrWhiteSpace(req.DeviceName) ? "TV" : req.DeviceName.Trim(),
                CreatedAt: DateTime.UtcNow,
                Status: "pending",
                UserId: null);

            var options = new MemoryCacheEntryOptions
            {
                SlidingExpiration  = PairTtl,
                AbsoluteExpirationRelativeToNow = PairTtl,
            };
            cache.Set(CacheKeyPrefix + code, entry, options);

            return Results.Ok(new PairInitDto(
                Code:      code,
                QrPayload: qrPayload,
                ExpiresAt: DateTime.UtcNow.Add(PairTtl)));
        }).AllowAnonymous();

        // ─── Poll: TV checks if a phone has confirmed ────────────────────
        // Critical detail: when the entry flips to "confirmed" we ALSO sign
        // the TV in here via SignInAsync. That issues the auth cookie on
        // the response — when the TV's poll loop sees status=confirmed and
        // does a full page reload, the cookie is already in the jar so /me
        // succeeds and the SPA routes to /. No second round-trip needed.
        app.MapGet(ApiRoutes.PairPoll, async (
            string code,
            IMemoryCache cache,
            IDbContextFactory<AppDbContext> dbFactory,
            AuthService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(code))
                return Results.BadRequest();
            var key = CacheKeyPrefix + code;
            if (!cache.TryGetValue(key, out PairRequest? entry) || entry is null)
                // 404 vs 410: 410 reserved for "we know it existed and is
                // expired". The cache transparently evicts on TTL, so by the
                // time we look it up we can't distinguish — return 410 either
                // way to push the TV's UI into the "expired, regenerate" state.
                return Results.Ok(new PairPollDto(Status: "expired", UserId: null));

            if (entry.Status == "confirmed" && entry.UserId is Guid uid)
            {
                // Hydrate the user (with role) and sign the TV in. The
                // response carries Set-Cookie for the same scheme used by
                // /api/auth/login — the TV's /pair page reloads / on
                // confirmed, which surfaces the cookie and lands on /catalog.
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var user = await db.Users.Include(u => u.Role)
                    .FirstOrDefaultAsync(u => u.Id == uid, ct);
                if (user is null)
                {
                    // The user vanished between confirm and poll (deleted by
                    // admin?). Drop the entry and tell the TV to start over.
                    cache.Remove(key);
                    return Results.Ok(new PairPollDto(Status: "expired", UserId: null));
                }
                await auth.SignInCookieAsync(http, user);
                // Consume the entry so a stolen code can't sign in a second
                // device. This is fine because the TV only polls until it
                // gets "confirmed" once.
                cache.Remove(key);
                return Results.Ok(new PairPollDto(Status: "confirmed", UserId: uid));
            }

            return Results.Ok(new PairPollDto(Status: "pending", UserId: null));
        }).AllowAnonymous();

        // ─── Confirm: phone (already signed in) authorises the TV ───────
        // Requires the phone's cookie. UserId is read from the request
        // identity, not the body, so a stale phone can't authorise the TV
        // as a different user.
        app.MapPost(ApiRoutes.PairConfirm, (
            ConfirmPairRequest req,
            IMemoryCache cache,
            IUserContext userCtx) =>
        {
            var uid = userCtx.CurrentUserId;
            if (uid is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Code)) return Results.BadRequest();

            var key = CacheKeyPrefix + req.Code;
            if (!cache.TryGetValue(key, out PairRequest? entry) || entry is null)
                return Results.NotFound();
            // Entry exists but has been consumed already — treat as gone so
            // the phone shows "code expired, ask the TV to refresh".
            if (entry.Status != "pending")
                return Results.StatusCode(StatusCodes.Status410Gone);

            var updated = entry with { Status = "confirmed", UserId = uid };
            // Keep the same sliding TTL so the TV's next poll picks it up.
            cache.Set(key, updated, new MemoryCacheEntryOptions
            {
                SlidingExpiration  = PairTtl,
                AbsoluteExpirationRelativeToNow = PairTtl,
            });
            return Results.NoContent();
        }).RequireAuthorization();

        // ─── QR image: server-generated PNG for the /pair page ──────────
        // Returns the QR encoding of the same animarr:// payload that
        // /pair/init handed back. Keeping QR generation server-side means
        // the WASM client doesn't have to ship a QR library.
        app.MapGet(ApiRoutes.PairQr, (
            string code,
            IMemoryCache cache,
            HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest();
            var key = CacheKeyPrefix + code;
            if (!cache.TryGetValue(key, out PairRequest? entry) || entry is null)
                return Results.NotFound();

            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var payload = $"animarr://pair?code={Uri.EscapeDataString(code)}&server={Uri.EscapeDataString(origin)}";

            using var generator = new QRCodeGenerator();
            // ECC level Q (~25% error tolerance) — survives the TV's display
            // not being pixel-perfect (motion blur on cheap panels, dust).
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            // PngByteQRCode produces a flat PNG byte array — no System.Drawing
            // dependency, works under Linux containers. 10px per module
            // yields a ~330x330 image including the quiet zone, which fits
            // the 300x300 design slot with a tiny margin to spare.
            using var png = new PngByteQRCode(data);
            var bytes = png.GetGraphic(pixelsPerModule: 10);

            return Results.File(bytes, "image/png");
        }).AllowAnonymous();

        return app;
    }

    /// <summary>
    /// 6-char dash-separated code (e.g. "4F-7K-92"). Reads cryptographic
    /// randomness because brute-forcing 6-of-31 alphabet entries from
    /// outside the LAN is conceivable; the 5-minute TTL limits the window
    /// but a higher-entropy code keeps the attack surface trivial.
    /// </summary>
    private static string GenerateCode()
    {
        Span<char> chars = stackalloc char[6];
        // RandomNumberGenerator.GetInt32 is rejection-sampled internally so
        // the distribution stays uniform across the 31-char alphabet.
        for (int i = 0; i < 6; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return $"{chars[0]}{chars[1]}-{chars[2]}{chars[3]}-{chars[4]}{chars[5]}";
    }

    /// <summary>
    /// Cache record for a pending pair. Immutable so the with-expression
    /// update path stays clean — no in-place mutation on a shared cache value.
    /// </summary>
    private sealed record PairRequest(
        string   Code,
        string   DeviceName,
        DateTime CreatedAt,
        string   Status,
        Guid?    UserId);
}
