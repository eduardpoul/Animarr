using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixFlatSectionUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FolderWatchers_Path",
                table: "FolderWatchers");

            migrationBuilder.CreateIndex(
                name: "IX_FolderWatchers_Path",
                table: "FolderWatchers",
                column: "Path",
                unique: true,
                filter: "\"SingleFilePath\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FolderWatchers_SingleFilePath",
                table: "FolderWatchers",
                column: "SingleFilePath",
                unique: true,
                filter: "\"SingleFilePath\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FolderWatchers_Path",
                table: "FolderWatchers");

            migrationBuilder.DropIndex(
                name: "IX_FolderWatchers_SingleFilePath",
                table: "FolderWatchers");

            migrationBuilder.CreateIndex(
                name: "IX_FolderWatchers_Path",
                table: "FolderWatchers",
                column: "Path",
                unique: true);
        }
    }
}
