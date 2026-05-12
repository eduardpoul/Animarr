using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRootFolderOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomRootFolderName",
                table: "TorrentRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SuppressRootFolder",
                table: "TorrentRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomRootFolderName",
                table: "TorrentRecords");

            migrationBuilder.DropColumn(
                name: "SuppressRootFolder",
                table: "TorrentRecords");
        }
    }
}
