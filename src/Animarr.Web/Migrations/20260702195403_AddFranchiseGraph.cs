using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddFranchiseGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AniListId",
                table: "WatchlistItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRelationsCheckAt",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FranchiseEdges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromAniListId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToAniListId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelationType = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FranchiseEdges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FranchiseNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AniListId = table.Column<int>(type: "INTEGER", nullable: false),
                    MalId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Episodes = table.Column<int>(type: "INTEGER", nullable: true),
                    CoverUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    FetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FranchiseNodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FranchiseEdges_FromAniListId_ToAniListId_RelationType",
                table: "FranchiseEdges",
                columns: new[] { "FromAniListId", "ToAniListId", "RelationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FranchiseEdges_ToAniListId",
                table: "FranchiseEdges",
                column: "ToAniListId");

            migrationBuilder.CreateIndex(
                name: "IX_FranchiseNodes_AniListId",
                table: "FranchiseNodes",
                column: "AniListId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FranchiseNodes_MalId",
                table: "FranchiseNodes",
                column: "MalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FranchiseEdges");

            migrationBuilder.DropTable(
                name: "FranchiseNodes");

            migrationBuilder.DropColumn(
                name: "AniListId",
                table: "WatchlistItems");

            migrationBuilder.DropColumn(
                name: "LastRelationsCheckAt",
                table: "MediaItems");
        }
    }
}
