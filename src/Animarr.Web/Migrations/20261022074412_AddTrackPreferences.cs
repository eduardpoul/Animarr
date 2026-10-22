using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    AudioKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    AudioLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    AudioTitle = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AudioCodec = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AudioChannels = table.Column<int>(type: "INTEGER", nullable: true),
                    AudioGroupKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SubtitleKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SubtitleLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SubtitleType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SubtitleTitle = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SubtitleGroupKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SubtitleOffsetMs = table.Column<int>(type: "INTEGER", nullable: true),
                    AudioOffsetsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Aspect = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackPreferences_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackPreferences_MediaItemId",
                table: "TrackPreferences",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackPreferences_UserId_MediaItemId",
                table: "TrackPreferences",
                columns: new[] { "UserId", "MediaItemId" },
                unique: true,
                filter: "\"Season\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TrackPreferences_UserId_MediaItemId_Season",
                table: "TrackPreferences",
                columns: new[] { "UserId", "MediaItemId", "Season" },
                unique: true,
                filter: "\"Season\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackPreferences");
        }
    }
}
