using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImageDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DescribeImages",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // hand-edited: on, like the C# default

            migrationBuilder.AddColumn<long>(
                name: "DescribeImagesFrom",
                table: "Settings",
                type: "INTEGER",
                nullable: true);

            // hand-edited: images already in NOMAD Inbox are only described with "Describe existing images",
            // so the existing settings row starts describing from the moment of this upgrade.
            var now = (long)new DateTimeOffsetToBinaryConverter().ConvertToProvider(DateTimeOffset.UtcNow)!;
            migrationBuilder.Sql($"UPDATE Settings SET DescribeImagesFrom = {now};");

            migrationBuilder.AddColumn<int>(
                name: "MaxImagesPerPage",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10); // hand-edited: like the C# default

            migrationBuilder.AddColumn<string>(
                name: "PublicBaseUrl",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VisionModel",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImageDescriptions",
                columns: table => new
                {
                    ImageKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: true),
                    ImageType = table.Column<string>(type: "TEXT", nullable: true),
                    Categories = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    ImageText = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DescribedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageDescriptions", x => x.ImageKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageDescriptions_Status",
                table: "ImageDescriptions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageDescriptions");

            migrationBuilder.DropColumn(
                name: "DescribeImages",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DescribeImagesFrom",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MaxImagesPerPage",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PublicBaseUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "VisionModel",
                table: "Settings");
        }
    }
}
