using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTrickplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastTrickplayScanAt",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrickplayAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    SpritePath = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalSec = table.Column<int>(type: "INTEGER", nullable: false),
                    TileWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    TileHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    Cols = table.Column<int>(type: "INTEGER", nullable: false),
                    Rows = table.Column<int>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSec = table.Column<double>(type: "REAL", nullable: false),
                    SourceWriteTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrickplayAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrickplayAssets_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrickplayAssets_MediaItemId_FilePath",
                table: "TrickplayAssets",
                columns: new[] { "MediaItemId", "FilePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrickplayAssets_MediaItemId_Season_Episode",
                table: "TrickplayAssets",
                columns: new[] { "MediaItemId", "Season", "Episode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrickplayAssets");

            migrationBuilder.DropColumn(
                name: "LastTrickplayScanAt",
                table: "MediaItems");
        }
    }
}
