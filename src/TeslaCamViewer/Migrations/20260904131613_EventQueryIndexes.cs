using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeslaCamViewer.Migrations
{
    /// <inheritdoc />
    public partial class EventQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Events_FolderName_Source",
                table: "Events",
                columns: new[] { "FolderName", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_TimeStamp",
                table: "Events",
                column: "TimeStamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_FolderName_Source",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_TimeStamp",
                table: "Events");
        }
    }
}
