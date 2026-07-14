using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddAiringSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiringStatus",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAiredAtUtc",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastAiredEpisode",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAiringCheckAt",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAirAtUtc",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NextEpisodeNumber",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiringStatus",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LastAiredAtUtc",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LastAiredEpisode",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LastAiringCheckAt",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "NextAirAtUtc",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "NextEpisodeNumber",
                table: "MediaItems");
        }
    }
}
