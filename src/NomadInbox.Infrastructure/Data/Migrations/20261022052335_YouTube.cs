using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class YouTube : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FollowCheckHours",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 6); // hand-edited default

            migrationBuilder.AddColumn<string>(
                name: "TranscriptLanguages",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "en"); // hand-edited default

            migrationBuilder.AddColumn<string>(
                name: "Author",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorId",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorUrl",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Chapters",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentSource",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationSeconds",
                table: "Links",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PublishedAt",
                table: "Links",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FollowedChannelId",
                table: "Emails",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Emails",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Email"); // hand-edited: existing rows are real emails

            migrationBuilder.CreateTable(
                name: "FollowedChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FollowedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    VideosFound = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowedChannels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FollowedChannels_ChannelId",
                table: "FollowedChannels",
                column: "ChannelId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FollowedChannels");

            migrationBuilder.DropColumn(
                name: "FollowCheckHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TranscriptLanguages",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Author",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "AuthorId",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "AuthorUrl",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "Chapters",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "ContentSource",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Links");

            migrationBuilder.DropColumn(
                name: "FollowedChannelId",
                table: "Emails");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Emails");
        }
    }
}
