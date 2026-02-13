using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeslaCamViewer.Migrations
{
    /// <inheritdoc />
    public partial class Minio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VideoData",
                table: "Cameras");

            migrationBuilder.AddColumn<string>(
                name: "BucketName",
                table: "Cameras",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "FileSize",
                table: "Cameras",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "MinioPath",
                table: "Cameras",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BucketName",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "FileSize",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "MinioPath",
                table: "Cameras");

            migrationBuilder.AddColumn<byte[]>(
                name: "VideoData",
                table: "Cameras",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
