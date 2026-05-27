using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WatchStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    IsWatched = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProgressMs = table.Column<long>(type: "INTEGER", nullable: true),
                    RuntimeMs = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalWatchTimeSec = table.Column<long>(type: "INTEGER", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchStates_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchStates_LastSeenAt",
                table: "WatchStates",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_WatchStates_MediaItemId",
                table: "WatchStates",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchStates_MediaItemId_Season_Episode",
                table: "WatchStates",
                columns: new[] { "MediaItemId", "Season", "Episode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchStates");
        }
    }
}
