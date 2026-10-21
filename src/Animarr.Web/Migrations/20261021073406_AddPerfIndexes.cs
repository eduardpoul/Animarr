using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Animarr.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPerfIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WatchEvents_UserId_Date",
                table: "WatchEvents");

            migrationBuilder.CreateIndex(
                name: "IX_WatchStates_UserId_LastSeenAt_Unwatched",
                table: "WatchStates",
                columns: new[] { "UserId", "LastSeenAt" },
                filter: "NOT \"IsWatched\"");

            migrationBuilder.CreateIndex(
                name: "IX_WatchEvents_UserId_Date_SecondsWatched_MediaItemId",
                table: "WatchEvents",
                columns: new[] { "UserId", "Date", "SecondsWatched", "MediaItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentificationQueues_QueuedAt",
                table: "IdentificationQueues",
                column: "QueuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_IdentificationQueues_Status_QueuedAt",
                table: "IdentificationQueues",
                columns: new[] { "Status", "QueuedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WatchStates_UserId_LastSeenAt_Unwatched",
                table: "WatchStates");

            migrationBuilder.DropIndex(
                name: "IX_WatchEvents_UserId_Date_SecondsWatched_MediaItemId",
                table: "WatchEvents");

            migrationBuilder.DropIndex(
                name: "IX_IdentificationQueues_QueuedAt",
                table: "IdentificationQueues");

            migrationBuilder.DropIndex(
                name: "IX_IdentificationQueues_Status_QueuedAt",
                table: "IdentificationQueues");

            migrationBuilder.CreateIndex(
                name: "IX_WatchEvents_UserId_Date",
                table: "WatchEvents",
                columns: new[] { "UserId", "Date" });
        }
    }
}
