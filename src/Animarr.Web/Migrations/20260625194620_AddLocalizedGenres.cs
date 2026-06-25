using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalizedGenres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenresLocalizedJson",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenresLocalizedJson",
                table: "MediaItems");
        }
    }
}
