using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoriesAndThemePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── UserPreferences columns ────────────────────────────────────
            // SQLite ALTER TABLE can't be conditional and pragma_table_info
            // can't be queried mid-DDL, so we use a TRY-style raw exec via
            // a sub-statement that checks pragma_table_info. We can't really
            // do that — the simplest robust approach is to use the EF default
            // path. These columns are brand-new to v5, can't pre-exist.
            migrationBuilder.AddColumn<string>(
                name: "HeroPagerStyle",
                table: "UserPreferences",
                type: "TEXT",
                nullable: false,
                defaultValue: "f");

            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "UserPreferences",
                type: "TEXT",
                nullable: false,
                defaultValue: "quietude");

            // ── Categories tables ─────────────────────────────────────────
            // These same tables were created by the deleted AddCategories
            // migration (20260526151816), which earlier deploys applied to
            // tower DB. If those tables already exist we silently skip the
            // CREATE so the migration is replay-safe. Raw SQL is the only
            // way to use IF NOT EXISTS in SQLite.

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""Categories"" (
                    ""Id""          TEXT    NOT NULL CONSTRAINT ""PK_Categories"" PRIMARY KEY,
                    ""Name""        TEXT    NOT NULL,
                    ""BuiltIn""     INTEGER NOT NULL,
                    ""Enabled""     INTEGER NOT NULL,
                    ""Description"" TEXT    NOT NULL,
                    ""Hint""        TEXT    NOT NULL,
                    ""SortOrder""   INTEGER NOT NULL,
                    ""CreatedAt""   TEXT    NOT NULL
                );");

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""MediaItemCategories"" (
                    ""MediaItemId"" TEXT NOT NULL,
                    ""CategoryId""  TEXT NOT NULL,
                    ""Source""      TEXT NOT NULL,
                    ""CreatedAt""   TEXT NOT NULL,
                    CONSTRAINT ""PK_MediaItemCategories"" PRIMARY KEY (""MediaItemId"", ""CategoryId""),
                    CONSTRAINT ""FK_MediaItemCategories_Categories_CategoryId""
                        FOREIGN KEY (""CategoryId"") REFERENCES ""Categories"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_MediaItemCategories_MediaItems_MediaItemId""
                        FOREIGN KEY (""MediaItemId"") REFERENCES ""MediaItems"" (""Id"") ON DELETE CASCADE
                );");

            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Categories_Name""
                    ON ""Categories"" (""Name"");");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_MediaItemCategories_CategoryId""
                    ON ""MediaItemCategories"" (""CategoryId"");");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_MediaItemCategories_CategoryId_MediaItemId""
                    ON ""MediaItemCategories"" (""CategoryId"", ""MediaItemId"");");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_MediaItemCategories_MediaItemId""
                    ON ""MediaItemCategories"" (""MediaItemId"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MediaItemCategories");
            migrationBuilder.DropTable(name: "Categories");
            migrationBuilder.DropColumn(name: "HeroPagerStyle", table: "UserPreferences");
            migrationBuilder.DropColumn(name: "Theme",          table: "UserPreferences");
        }
    }
}
