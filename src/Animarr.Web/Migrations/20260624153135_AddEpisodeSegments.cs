using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodeSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EpisodeSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: false),
                    Episode = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    StartSec = table.Column<double>(type: "REAL", nullable: false),
                    EndSec = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpisodeSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EpisodeSegments_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeSegments_MediaItemId_Season_Episode_Kind",
                table: "EpisodeSegments",
                columns: new[] { "MediaItemId", "Season", "Episode", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpisodeSegments");
        }
    }
}
