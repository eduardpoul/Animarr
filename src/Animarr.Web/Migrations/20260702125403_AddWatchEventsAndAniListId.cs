using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchEventsAndAniListId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AniListId",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WatchEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SecondsWatched = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchEvents_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchEvents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_AniListId",
                table: "MediaItems",
                column: "AniListId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchEvents_MediaItemId",
                table: "WatchEvents",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchEvents_UserId_Date",
                table: "WatchEvents",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchEvents_UserId_MediaItemId_Season_Episode_Date",
                table: "WatchEvents",
                columns: new[] { "UserId", "MediaItemId", "Season", "Episode", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchEvents");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_AniListId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "AniListId",
                table: "MediaItems");
        }
    }
}
