using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class FlipPagerDefaultToChevrons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only migration: the hero pager default changed from "f"
            // (transparent named strokes) to "g" (Chevrons). Existing rows were
            // stamped with the old column default "f" at creation time (see
            // AddCategoriesAndThemePreferences, defaultValue:"f"), so without
            // this they'd stay on strokes. Flip the ones still on the old default
            // over to the new one; explicit "h" (Numbered) picks are left alone.
            migrationBuilder.Sql("UPDATE UserPreferences SET HeroPagerStyle = 'g' WHERE HeroPagerStyle = 'f';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort inverse — moves the chevrons users back to strokes.
            // (Can't distinguish migrated rows from explicit "g" picks, but a
            // rollback to before chevrons-as-default implies strokes again.)
            migrationBuilder.Sql("UPDATE UserPreferences SET HeroPagerStyle = 'f' WHERE HeroPagerStyle = 'g';");
        }
    }
}
