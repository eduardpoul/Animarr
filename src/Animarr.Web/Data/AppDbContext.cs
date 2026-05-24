using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<FolderWatcher> FolderWatchers => Set<FolderWatcher>();
    public DbSet<RenamePattern> RenamePatterns => Set<RenamePattern>();
    public DbSet<IgnoreRule> IgnoreRules => Set<IgnoreRule>();
    public DbSet<RenameHistory> RenameHistories => Set<RenameHistory>();
    public DbSet<TorrentRecord> TorrentRecords => Set<TorrentRecord>();
    public DbSet<TorrentFileSelection> TorrentFileSelections => Set<TorrentFileSelection>();
    public DbSet<TorrentConfig> TorrentConfig => Set<TorrentConfig>();
    public DbSet<RenameQueue> RenameQueues => Set<RenameQueue>();

    // ─── Catalog / Media ──────────────────────────────────────────────────────
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<MediaTag> MediaTags => Set<MediaTag>();
    public DbSet<MediaItemTag> MediaItemTags => Set<MediaItemTag>();
    public DbSet<IdentificationQueue> IdentificationQueues => Set<IdentificationQueue>();
    public DbSet<WatchState> WatchStates => Set<WatchState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FolderWatcher>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            // Only enforce unique Path for normal folders (not flat-file entries which share the section path)
            e.HasIndex(x => x.Path).IsUnique().HasFilter("\"SingleFilePath\" IS NULL");
            // Flat-file entries are uniquely identified by their file path
            e.HasIndex(x => x.SingleFilePath).IsUnique().HasFilter("\"SingleFilePath\" IS NOT NULL");
        });

        modelBuilder.Entity<RenamePattern>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.Folder)
             .WithMany(f => f.Patterns)
             .HasForeignKey(x => x.FolderId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IgnoreRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.Folder)
             .WithMany(f => f.IgnoreRules)
             .HasForeignKey(x => x.FolderId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RenameHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.Folder)
             .WithMany(f => f.History)
             .HasForeignKey(x => x.FolderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TorrentRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.InfoHash).IsUnique();
            e.HasOne(x => x.FolderWatcher)
             .WithMany()
             .HasForeignKey(x => x.FolderWatcherId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TorrentFileSelection>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.Torrent)
             .WithMany(t => t.FileSelections)
             .HasForeignKey(x => x.TorrentId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TorrentConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<RenameQueue>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.Folder)
             .WithMany()
             .HasForeignKey(x => x.FolderId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.FilePath, x.Status });
        });

        // ─── Catalog / Media ─────────────────────────────────────────────────

        modelBuilder.Entity<AppConfig>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
        });

        modelBuilder.Entity<MediaItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.Folder)
             .WithMany()
             .HasForeignKey(x => x.FolderId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.FolderId).IsUnique();
            e.HasIndex(x => x.TmdbId);
            e.HasIndex(x => x.MalId);
            e.HasIndex(x => x.IdentificationStatus);
        });

        modelBuilder.Entity<MediaTag>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<MediaItemTag>(e =>
        {
            e.HasKey(x => new { x.MediaItemId, x.MediaTagId });
            e.HasOne(x => x.MediaItem)
             .WithMany(m => m.Tags)
             .HasForeignKey(x => x.MediaItemId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.MediaTag)
             .WithMany(t => t.Items)
             .HasForeignKey(x => x.MediaTagId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IdentificationQueue>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.Folder)
             .WithMany()
             .HasForeignKey(x => x.FolderId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.FolderId, x.Status });
        });

        modelBuilder.Entity<WatchState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.MediaItem)
             .WithMany()
             .HasForeignKey(x => x.MediaItemId)
             .OnDelete(DeleteBehavior.Cascade);
            // One state row per (item, season, episode). Movies use NULL/NULL
            // and therefore are limited to a single row per MediaItem. SQLite
            // treats NULL as distinct, so we still need a filtered index for
            // the movie case to keep the "one row per movie" invariant.
            e.HasIndex(x => new { x.MediaItemId, x.Season, x.Episode }).IsUnique();
            e.HasIndex(x => x.MediaItemId);
            e.HasIndex(x => x.LastSeenAt);
        });
    }
}
