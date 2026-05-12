using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentifyEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IdentifyEnabled",
                table: "FolderWatchers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdentifyEnabled",
                table: "FolderWatchers");
        }
    }
}
