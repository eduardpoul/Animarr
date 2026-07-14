using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEpisodeListView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EpisodeListView",
                table: "UserPreferences",
                type: "TEXT",
                nullable: false,
                // Match the model default so existing rows upgrade to the grid
                // layout (EF doesn't read the C# initializer for the column
                // default). Mirrors HeroPagerStyle="f" / Theme="quietude".
                defaultValue: "grid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpisodeListView",
                table: "UserPreferences");
        }
    }
}
