using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    ParentSectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderWatchers", x => x.Id);
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
                    CacheDirectory = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentConfig", x => x.Id);
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
                name: "TorrentFileSelections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TorrentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false)
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
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IgnoreRules_FolderId",
                table: "IgnoreRules",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_RenameHistories_FolderId",
                table: "RenameHistories",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_RenamePatterns_FolderId",
                table: "RenamePatterns",
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
                name: "IgnoreRules");

            migrationBuilder.DropTable(
                name: "RenameHistories");

            migrationBuilder.DropTable(
                name: "RenamePatterns");

            migrationBuilder.DropTable(
                name: "TorrentConfig");

            migrationBuilder.DropTable(
                name: "TorrentFileSelections");

            migrationBuilder.DropTable(
                name: "TorrentRecords");

            migrationBuilder.DropTable(
                name: "FolderWatchers");
        }
    }
}
