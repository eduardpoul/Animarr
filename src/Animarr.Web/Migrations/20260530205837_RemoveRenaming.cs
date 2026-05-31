using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRenaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IgnoreRules");

            migrationBuilder.DropTable(
                name: "RenameHistories");

            migrationBuilder.DropTable(
                name: "RenameQueues");

            migrationBuilder.DropColumn(
                name: "AutoRenameAfterDownload",
                table: "TorrentConfig");

            migrationBuilder.DropColumn(
                name: "RenameEnabled",
                table: "FolderWatchers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRenameAfterDownload",
                table: "TorrentConfig",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RenameEnabled",
                table: "FolderWatchers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "IgnoreRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Mask = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false)
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
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    IsReverted = table.Column<bool>(type: "INTEGER", nullable: false),
                    NewPath = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalPath = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
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
                name: "RenameQueues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_IgnoreRules_FolderId",
                table: "IgnoreRules",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_RenameHistories_FolderId",
                table: "RenameHistories",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_RenameQueues_FilePath_Status",
                table: "RenameQueues",
                columns: new[] { "FilePath", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RenameQueues_FolderId",
                table: "RenameQueues",
                column: "FolderId");
        }
    }
}
