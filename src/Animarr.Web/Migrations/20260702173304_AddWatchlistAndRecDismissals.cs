using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchlistAndRecDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecDismissals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecDismissals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecDismissals_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecDismissals_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchlistItems_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchlistItems_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecDismissals_MediaItemId",
                table: "RecDismissals",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RecDismissals_UserId_MediaItemId",
                table: "RecDismissals",
                columns: new[] { "UserId", "MediaItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecDismissals_UserId_TmdbId",
                table: "RecDismissals",
                columns: new[] { "UserId", "TmdbId" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_MediaItemId",
                table: "WatchlistItems",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_UserId_MediaItemId",
                table: "WatchlistItems",
                columns: new[] { "UserId", "MediaItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_UserId_TmdbId",
                table: "WatchlistItems",
                columns: new[] { "UserId", "TmdbId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecDismissals");

            migrationBuilder.DropTable(
                name: "WatchlistItems");
        }
    }
}
