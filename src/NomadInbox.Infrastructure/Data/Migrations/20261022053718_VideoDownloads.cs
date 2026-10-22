using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VideoDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxVideoHeight",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 720); // hand-edited default

            migrationBuilder.AddColumn<int>(
                name: "MaxVideoMegabytes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2048); // hand-edited default

            migrationBuilder.AddColumn<string>(
                name: "VideoDownloadMode",
                table: "Settings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Tagged"); // hand-edited default

            migrationBuilder.AddColumn<long>(
                name: "VideoBytes",
                table: "Links",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoError",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoPath",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoStatus",
                table: "Links",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "None"); // hand-edited default

            migrationBuilder.CreateIndex(
                name: "IX_Links_VideoStatus",
                table: "Links",
                column: "VideoStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Links_VideoStatus",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "MaxVideoHeight",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MaxVideoMegabytes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "VideoDownloadMode",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "VideoBytes",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "VideoError",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "VideoPath",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "VideoStatus",
                table: "Links");
        }
    }
}
