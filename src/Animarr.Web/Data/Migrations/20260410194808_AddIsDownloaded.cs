using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsDownloaded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDownloaded",
                table: "TorrentFileSelections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDownloaded",
                table: "TorrentFileSelections");
        }
    }
}
