using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiUserAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WatchStates_MediaItemId_Season_Episode",
                table: "WatchStates");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "WatchStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PermViewContent = table.Column<bool>(type: "INTEGER", nullable: false),
                    PermUploadContent = table.Column<bool>(type: "INTEGER", nullable: false),
                    PermSystemSettings = table.Column<bool>(type: "INTEGER", nullable: false),
                    PermManageUsers = table.Column<bool>(type: "INTEGER", nullable: false),
                    FoldersJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarPath = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarHue = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserFavorites",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFavorites", x => new { x.UserId, x.MediaItemId });
                    table.ForeignKey(
                        name: "FK_UserFavorites_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserFavorites_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccentHue = table.Column<int>(type: "INTEGER", nullable: false),
                    BackdropEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    BackdropBlurPx = table.Column<int>(type: "INTEGER", nullable: false),
                    BackdropBrightness = table.Column<int>(type: "INTEGER", nullable: false),
                    BackdropIntervalSec = table.Column<int>(type: "INTEGER", nullable: false),
                    TvMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    AudioPreferredLanguage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SubtitlePreferredLanguage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SubtitleSize = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultVolume = table.Column<int>(type: "INTEGER", nullable: false),
                    AudioPassthrough = table.Column<bool>(type: "INTEGER", nullable: false),
                    NormalizeVolume = table.Column<bool>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchStates_UserId",
                table: "WatchStates",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchStates_UserId_MediaItemId_Season_Episode",
                table: "WatchStates",
                columns: new[] { "UserId", "MediaItemId", "Season", "Episode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_MediaItemId",
                table: "UserFavorites",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFavorites_UserId",
                table: "UserFavorites",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleId",
                table: "Users",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WatchStates_Users_UserId",
                table: "WatchStates",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WatchStates_Users_UserId",
                table: "WatchStates");

            migrationBuilder.DropTable(
                name: "UserFavorites");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_WatchStates_UserId",
                table: "WatchStates");

            migrationBuilder.DropIndex(
                name: "IX_WatchStates_UserId_MediaItemId_Season_Episode",
                table: "WatchStates");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "WatchStates");

            migrationBuilder.CreateIndex(
                name: "IX_WatchStates_MediaItemId_Season_Episode",
                table: "WatchStates",
                columns: new[] { "MediaItemId", "Season", "Episode" },
                unique: true);
        }
    }
}
