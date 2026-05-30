using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddThemeMusic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ThemeMusicEnabled",
                table: "UserPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ThemeMusicVolume",
                table: "UserPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 40);

            migrationBuilder.AddColumn<string>(
                name: "ThemePath",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThemeTitle",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThemeMusicEnabled",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "ThemeMusicVolume",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "ThemePath",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ThemeTitle",
                table: "MediaItems");
        }
    }
}
