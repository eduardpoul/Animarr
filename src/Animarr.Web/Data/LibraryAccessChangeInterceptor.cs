using System.Runtime.CompilerServices;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Animarr.Web.Data;

/// <summary>
/// Change counter for the rows that decide who may read which library file:
/// <see cref="FolderWatcher"/> (the library roots), <see cref="User"/> (which
/// role an account holds) and <see cref="Role"/> (folder allowlists).
/// <c>MediaPathValidator</c> caches those rows in memory for every
/// byte request; it reloads whenever this number has moved since its load.
/// </summary>
public sealed class LibraryAccessVersion
{
    private long _value;

    public long Current => Interlocked.Read(ref _value);

    /// <summary>Invalidate every cache keyed on <see cref="Current"/>. Writers
    /// that bypass the change tracker (ExecuteUpdate/ExecuteDelete/raw SQL on
    /// those tables) must call this after their statement commits.</summary>
    public void Bump() => Interlocked.Increment(ref _value);
}

/// <summary>
/// Bumps <see cref="LibraryAccessVersion"/> after every SaveChanges that
/// added, modified or deleted a FolderWatcher, User or Role — so a removed
/// folder, a narrowed role or a deleted account stops being honoured by the
/// cached access check on the very next request, not after a TTL.
///
/// The bump happens AFTER the commit (SavedChanges), never before: a reader
/// that reloads between a pre-commit bump and the commit would cache the old
/// rows under the new version. Readers record the version before they query,
/// so any load that overlapped the commit is already stale by the time the
/// post-commit bump lands. The change set is captured in SavingChanges (the
/// only point where Deleted entries are still visible) and carried over per
/// context instance. Stateless towards EF itself, so it's safe to share
/// across the context pool.
/// </summary>
public sealed class LibraryAccessChangeInterceptor(LibraryAccessVersion version) : SaveChangesInterceptor
{
    private static readonly object Marker = new();

    // Contexts whose in-flight SaveChanges touches access rows. Weak keys: a
    // context that never reports completion can't leak an entry.
    private readonly ConditionalWeakTable<DbContext, object> _pending = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Complete(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Complete(eventData.Context);
        return ValueTask.FromResult(result);
    }

    // A failed or cancelled save committed nothing, but bumping costs one
    // reload — cheaper than reasoning about partial outcomes.
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Complete(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Complete(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Complete(eventData.Context);

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Complete(eventData.Context);
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        if (context is null) return;
        // Entries() runs DetectChanges first, so plain property edits on
        // tracked entities are already reported as Modified here.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is FolderWatcher or User or Role)
            {
                _pending.AddOrUpdate(context, Marker);
                return;
            }
        }
    }

    private void Complete(DbContext? context)
    {
        if (context is not null && _pending.Remove(context))
            version.Bump();
    }
}
