using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowPrivateNetworkLinks",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Hand-edited defaults so the existing settings row and links match the C# initializers.
            migrationBuilder.AddColumn<int>(
                name: "OllamaContextTokens",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8192);

            migrationBuilder.AddColumn<string>(
                name: "OllamaModel",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OllamaUrl",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "http://host.docker.internal:11434");

            migrationBuilder.AddColumn<bool>(
                name: "SummariesEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SummaryPrompt",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Links",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ContentText",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FetchedAt",
                table: "Links",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalUrl",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SiteName",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Links",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<long>(
                name: "SummarizedAt",
                table: "Links",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SummaryModel",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Links_Status",
                table: "Links",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Links_Status",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "AllowPrivateNetworkLinks",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "OllamaContextTokens",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "OllamaModel",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "OllamaUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SummariesEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SummaryPrompt",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "ContentText",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "FetchedAt",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "FinalUrl",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "SiteName",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "SummarizedAt",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "SummaryModel",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Links");
        }
    }
}
