using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<FolderWatcher> FolderWatchers => Set<FolderWatcher>();
    public DbSet<RenamePattern> RenamePatterns => Set<RenamePattern>();
    public DbSet<TorrentRecord> TorrentRecords => Set<TorrentRecord>();
    public DbSet<TorrentFileSelection> TorrentFileSelections => Set<TorrentFileSelection>();
    public DbSet<TorrentConfig> TorrentConfig => Set<TorrentConfig>();

    // ─── Catalog / Media ──────────────────────────────────────────────────────
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<MediaTag> MediaTags => Set<MediaTag>();
    public DbSet<MediaItemTag> MediaItemTags => Set<MediaItemTag>();
    public DbSet<IdentificationQueue> IdentificationQueues => Set<IdentificationQueue>();
    public DbSet<WatchState> WatchStates => Set<WatchState>();
    public DbSet<EpisodeFileMapping> EpisodeFileMappings => Set<EpisodeFileMapping>();
    public DbSet<EpisodeSegment> EpisodeSegments => Set<EpisodeSegment>();
    public DbSet<EpisodeMetadata> EpisodeMetadata => Set<EpisodeMetadata>();

    // ─── Multi-user (v4) ──────────────────────────────────────────────────────
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserPreferences> UserPreferences => Set<UserPreferences>();
    public DbSet<UserFavorite> UserFavorites => Set<UserFavorite>();

    // ─── Categories ────────────────────────────────────────────────────────────
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<MediaItemCategory> MediaItemCategories => Set<MediaItemCategory>();

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
            // v4: scope per user. SetNull on user delete so the data isn't lost
            // — admin can reassign orphan rows later if desired.
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
            // One state row per (user, item, season, episode). Movies use
            // NULL/NULL for season/episode and therefore are limited to a
            // single row per (user, MediaItem). SQLite treats NULL as
            // distinct in indexes — the v4 unique constraint includes UserId
            // so two users can independently mark the same episode.
            e.HasIndex(x => new { x.UserId, x.MediaItemId, x.Season, x.Episode }).IsUnique();
            e.HasIndex(x => x.MediaItemId);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.LastSeenAt);
        });

        modelBuilder.Entity<EpisodeFileMapping>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Source).HasMaxLength(16).IsRequired();
            e.HasOne(x => x.MediaItem)
             .WithMany()
             .HasForeignKey(x => x.MediaItemId)
             .OnDelete(DeleteBehavior.Cascade);
            // One override per (item, file). Upserts target this key.
            e.HasIndex(x => new { x.MediaItemId, x.FilePath }).IsUnique();
        });

        modelBuilder.Entity<EpisodeSegment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.MediaItem)
             .WithMany()
             .HasForeignKey(x => x.MediaItemId)
             .OnDelete(DeleteBehavior.Cascade);
            // One segment per (item, season, episode, kind). The detection pass
            // upserts against this key, so re-running is idempotent.
            e.HasIndex(x => new { x.MediaItemId, x.Season, x.Episode, x.Kind }).IsUnique();
        });

        modelBuilder.Entity<EpisodeMetadata>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.MediaItem)
             .WithMany()
             .HasForeignKey(x => x.MediaItemId)
             .OnDelete(DeleteBehavior.Cascade);
            // One metadata row per (item, TMDB season, TMDB episode). The lazy
            // fetch replaces the item's rows wholesale, but the unique key keeps
            // the table clean if a partial write ever races.
            e.HasIndex(x => new { x.MediaItemId, x.Season, x.Episode }).IsUnique();
        });

        // ─── Multi-user (v4) ────────────────────────────────────────────────

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(128).IsRequired();
            // Case-insensitive uniqueness — usernames are stored lowercase by
            // AuthService.NormaliseUsername. The unique index on the lowered
            // value gives us "alice == ALICE == Alice" with no extra work.
            e.HasIndex(x => x.Username).IsUnique();
            e.HasOne(x => x.Role)
             .WithMany()
             .HasForeignKey(x => x.RoleId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasMaxLength(256);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<UserPreferences>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.AudioPreferredLanguage).HasMaxLength(32);
            e.Property(x => x.SubtitlePreferredLanguage).HasMaxLength(32);
            e.Property(x => x.Language).HasMaxLength(8);
        });

        modelBuilder.Entity<UserFavorite>(e =>
        {
            e.HasKey(x => new { x.UserId, x.MediaItemId });
            e.HasOne(x => x.User)
             .WithMany()
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.MediaItem)
             .WithMany()
             .HasForeignKey(x => x.MediaItemId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });

        // ─── Categories ────────────────────────────────────────────────────────
        modelBuilder.Entity<Category>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasMaxLength(256);
            e.Property(x => x.Hint).HasMaxLength(512);
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<MediaItemCategory>(e =>
        {
            // Composite PK so the same (item, category) pair can't be inserted twice.
            e.HasKey(x => new { x.MediaItemId, x.CategoryId });
            e.Property(x => x.Source).HasMaxLength(16).IsRequired();
            e.HasOne(x => x.MediaItem)
             .WithMany(m => m.Categories)
             .HasForeignKey(x => x.MediaItemId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Category)
             .WithMany(c => c.Items)
             .HasForeignKey(x => x.CategoryId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.MediaItemId);
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => new { x.CategoryId, x.MediaItemId });
        });
    }
}
