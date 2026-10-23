using Android.Content;
using Android.Database;
using Android.OS;
using Animarr.App.Services;

namespace Animarr.App;

/// <summary>
/// The pictures of the launcher's "Continue watching" cards
/// (<see cref="WatchNextService"/>): the launcher loads a card's picture by its
/// URI itself, and the server's images need the app's media token — so the
/// app caches each card's picture and serves it here,
/// <c>content://com.animarr.app.watchnext/{sha1}</c>. Read-only, only those
/// cached files (a name is exactly 40 hex digits); exported, as a picture on
/// the home screen is shown to anyone anyway.
/// </summary>
[ContentProvider(new[] { WatchNextImages.Authority }, Name = "com.animarr.app.WatchNextImageProvider",
    Exported = true, GrantUriPermissions = false)]
public sealed class WatchNextImageProvider : ContentProvider
{
    public override bool OnCreate() => true;

    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        if (mode != "r") throw new Java.Lang.SecurityException("read-only");
        var file = FileOf(uri) ?? throw new Java.IO.FileNotFoundException(uri.ToString());
        return ParcelFileDescriptor.Open(new Java.IO.File(file), ParcelFileMode.ReadOnly);
    }

    public override string? GetType(Android.Net.Uri uri)
    {
        if (FileOf(uri) is not { } file) return null;
        try
        {
            using var fs = File.OpenRead(file);
            var head = new byte[12];
            var n = fs.Read(head, 0, head.Length);
            return WatchNextImages.MimeOf(head.AsSpan(0, n)) ?? "image/jpeg";
        }
        catch { return "image/jpeg"; }
    }

    private string? FileOf(Android.Net.Uri uri)
    {
        var name = uri.LastPathSegment;
        if (!WatchNextImages.IsName(name) || Context is not { } ctx) return null;
        var file = Path.Combine(WatchNextService.ImageDir(ctx), name!);
        return File.Exists(file) ? file : null;
    }

    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder) => null;
    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => null;
    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;
}
