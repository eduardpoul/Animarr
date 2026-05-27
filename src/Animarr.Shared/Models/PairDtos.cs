namespace Animarr.Shared.Models;

/// <summary>
/// Response of <see cref="ApiRoutes.PairInit"/>. The TV displays the
/// human-readable <see cref="Code"/> and either renders <see cref="QrPayload"/>
/// directly into a QR (client-side) or, more commonly, fetches the server-
/// generated image via <see cref="ApiRoutes.PairQr"/> using the same code.
/// </summary>
public sealed record PairInitDto(
    /// <summary>Six characters in three pairs separated by dashes, e.g.
    /// <c>4F-7K-92</c>. Alphabet excludes I/O/0/1 for legibility.</summary>
    string   Code,

    /// <summary>The string the QR encodes: <c>animarr://pair?code=...&amp;server=...</c>.
    /// The MAUI app deep-links into <c>/login</c> with the code pre-filled; a
    /// phone browser falls back to the visible code-entry path.</summary>
    string   QrPayload,

    /// <summary>UTC instant after which the code stops working. The TV uses
    /// this to render an expiring countdown so the user doesn't try to scan
    /// a stale QR.</summary>
    DateTime ExpiresAt);

/// <summary>
/// Response of <see cref="ApiRoutes.PairPoll"/>. The TV polls until status
/// flips to <c>"confirmed"</c>, at which point the response carries Set-Cookie
/// for the matching user and the TV reloads <c>/</c>.
/// </summary>
public sealed record PairPollDto(
    /// <summary>One of <c>"pending"</c>, <c>"confirmed"</c>, <c>"expired"</c>.</summary>
    string Status,
    /// <summary>Identity that authorised this pair, populated only when
    /// <see cref="Status"/> = <c>"confirmed"</c>. Useful for telemetry — the
    /// TV's UI doesn't need to read it because the cookie carries the same id.</summary>
    Guid?  UserId);
