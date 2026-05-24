using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppConfigs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppConfigs", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "FolderWatchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    WatchEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FolderType = table.Column<int>(type: "INTEGER", nullable: false),
                    RenameEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSection = table.Column<bool>(type: "INTEGER", nullable: false),
                    IdentifyEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FlatSection = table.Column<bool>(type: "INTEGER", nullable: false),
                    SingleFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    ParentSectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Hue = table.Column<int>(type: "INTEGER", nullable: true),
                    BackdropPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderWatchers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAutoTag = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaTags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TorrentConfig",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    GlobalDownloadLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    GlobalUploadLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    ListenPort = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConnections = table.Column<int>(type: "INTEGER", nullable: false),
                    EnableDHT = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableLSD = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnableUPnP = table.Column<bool>(type: "INTEGER", nullable: false),
                    StopSeedingAfterDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    StopSeedingRatio = table.Column<double>(type: "REAL", nullable: false),
                    AutoRenameAfterDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultStopAfterDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    CacheDirectory = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentificationQueues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ForceRefresh = table.Column<bool>(type: "INTEGER", nullable: false),
                    LogDetails = table.Column<string>(type: "TEXT", nullable: true),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentificationQueues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentificationQueues_FolderWatchers_FolderId",
                        column: x => x.FolderId,
                        principalTable: "FolderWatchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IgnoreRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Mask = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IgnoreRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IgnoreRules_FolderWatchers_FolderId",
                        column: x => x.FolderId,
                        principalTable: "FolderWatchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    CjkTitle = table.Column<string>(type: "TEXT", nullable: true),
                    EnglishTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    MalId = table.Column<int>(type: "INTEGER", nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", nullable: true),
                    TvdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", nullable: true),
                    FanartPath = table.Column<string>(type: "TEXT", nullable: true),
                    LogoPath = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Tagline = table.Column<string>(type: "TEXT", nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    RatingCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Popularity = table.Column<double>(type: "REAL", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    ContentRating = table.Column<string>(type: "TEXT", nullable: true),
                    Runtime = table.Column<int>(type: "INTEGER", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Studio = table.Column<string>(type: "TEXT", nullable: true),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SeasonLabel = table.Column<string>(type: "TEXT", nullable: true),
                    Hue = table.Column<int>(type: "INTEGER", nullable: true),
                    SeasonsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: true),
                    TmdbConfidence = table.Column<double>(type: "REAL", nullable: true),
                    MalConfidence = table.Column<double>(type: "REAL", nullable: true),
                    ImdbConfidence = table.Column<double>(type: "REAL", nullable: true),
                    LlmIdentifiedTitle = table.Column<string>(type: "TEXT", nullable: true),
                    LlmConfidence = table.Column<double>(type: "REAL", nullable: true),
                    IdentificationStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastMetadataRefreshedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaItems_FolderWatchers_FolderId",
                        column: x => x.FolderId,
                        principalTable: "FolderWatchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RenameHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalPath = table.Column<string>(type: "TEXT", nullable: false),
                    NewPath = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsReverted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RenameHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RenameHistories_FolderWatchers_FolderId",
                        column: x => x.FolderId,
                        principalTable: "FolderWatchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RenamePatterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    IsExcluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    GlobalPatternId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApplicableTo = table.Column<int>(type: "INTEGER", nullable: true),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RenamePatterns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RenamePatterns_FolderWatchers_FolderId",
                        column: x => x.FolderId,
                        principalTable: "FolderWatchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RenameQueues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RenameQueues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RenameQueues_FolderWatchers_FolderId",
                        column: x => x.FolderId,
                        principalTable: "FolderWatchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TorrentRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InfoHash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MagnetLink = table.Column<string>(type: "TEXT", nullable: true),
                    TorrentFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    SavePath = table.Column<string>(type: "TEXT", nullable: false),
                    FolderWatcherId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Downloaded = table.Column<long>(type: "INTEGER", nullable: false),
                    Uploaded = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    StopSeedingRatio = table.Column<double>(type: "REAL", nullable: true),
                    StopAfterDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoRename = table.Column<bool>(type: "INTEGER", nullable: false),
                    FlattenSubfolders = table.Column<bool>(type: "INTEGER", nullable: false),
                    SuppressRootFolder = table.Column<bool>(type: "INTEGER", nullable: false),
                    CustomRootFolderName = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TorrentRecords_FolderWatchers_FolderWatcherId",
                        column: x => x.FolderWatcherId,
                        principalTable: "FolderWatchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MediaItemTags",
                columns: table => new
                {
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaTagId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItemTags", x => new { x.MediaItemId, x.MediaTagId });
                    table.ForeignKey(
                        name: "FK_MediaItemTags_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaItemTags_MediaTags_MediaTagId",
                        column: x => x.MediaTagId,
                        principalTable: "MediaTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TorrentFileSelections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TorrentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDownloaded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentFileSelections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TorrentFileSelections_TorrentRecords_TorrentId",
                        column: x => x.TorrentId,
                        principalTable: "TorrentRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FolderWatchers_Path",
                table: "FolderWatchers",
                column: "Path",
                unique: true,
                filter: "\"SingleFilePath\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FolderWatchers_SingleFilePath",
                table: "FolderWatchers",
                column: "SingleFilePath",
                unique: true,
                filter: "\"SingleFilePath\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentificationQueues_FolderId_Status",
                table: "IdentificationQueues",
                columns: new[] { "FolderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IgnoreRules_FolderId",
                table: "IgnoreRules",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_FolderId",
                table: "MediaItems",
                column: "FolderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_IdentificationStatus",
                table: "MediaItems",
                column: "IdentificationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_MalId",
                table: "MediaItems",
                column: "MalId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_TmdbId",
                table: "MediaItems",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemTags_MediaTagId",
                table: "MediaItemTags",
                column: "MediaTagId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaTags_Name",
                table: "MediaTags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RenameHistories_FolderId",
                table: "RenameHistories",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_RenamePatterns_FolderId",
                table: "RenamePatterns",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_RenameQueues_FilePath_Status",
                table: "RenameQueues",
                columns: new[] { "FilePath", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RenameQueues_FolderId",
                table: "RenameQueues",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_TorrentFileSelections_TorrentId",
                table: "TorrentFileSelections",
                column: "TorrentId");

            migrationBuilder.CreateIndex(
                name: "IX_TorrentRecords_FolderWatcherId",
                table: "TorrentRecords",
                column: "FolderWatcherId");

            migrationBuilder.CreateIndex(
                name: "IX_TorrentRecords_InfoHash",
                table: "TorrentRecords",
                column: "InfoHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppConfigs");

            migrationBuilder.DropTable(
                name: "IdentificationQueues");

            migrationBuilder.DropTable(
                name: "IgnoreRules");

            migrationBuilder.DropTable(
                name: "MediaItemTags");

            migrationBuilder.DropTable(
                name: "RenameHistories");

            migrationBuilder.DropTable(
                name: "RenamePatterns");

            migrationBuilder.DropTable(
                name: "RenameQueues");

            migrationBuilder.DropTable(
                name: "TorrentConfig");

            migrationBuilder.DropTable(
                name: "TorrentFileSelections");

            migrationBuilder.DropTable(
                name: "MediaItems");

            migrationBuilder.DropTable(
                name: "MediaTags");

            migrationBuilder.DropTable(
                name: "TorrentRecords");

            migrationBuilder.DropTable(
                name: "FolderWatchers");
        }
    }
}
