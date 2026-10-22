using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Processing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited defaults so an existing settings row gets the same values as InboxSettings' initializers.
            migrationBuilder.AddColumn<bool>(
                name: "ApplyLabels",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "FailedLabel",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "NOMAD/Failed");

            migrationBuilder.AddColumn<int>(
                name: "MaxAttachmentMegabytes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<string>(
                name: "ProcessedLabel",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "NOMAD/Processed");

            migrationBuilder.AddColumn<int>(
                name: "AttachmentCount",
                table: "Emails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Emails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "BodyText",
                table: "Emails",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkCount",
                table: "Emails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ProcessedAt",
                table: "Emails",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "UidValidity",
                table: "Emails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InboxEmailId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    StoredPath = table.Column<string>(type: "TEXT", nullable: true),
                    IsDuplicate = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExtractedText = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Emails_InboxEmailId",
                        column: x => x.InboxEmailId,
                        principalTable: "Emails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Links",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InboxEmailId = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Links_Emails_InboxEmailId",
                        column: x => x.InboxEmailId,
                        principalTable: "Emails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_InboxEmailId",
                table: "Attachments",
                column: "InboxEmailId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_Sha256",
                table: "Attachments",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Links_InboxEmailId",
                table: "Links",
                column: "InboxEmailId");

            migrationBuilder.CreateIndex(
                name: "IX_Links_Kind",
                table: "Links",
                column: "Kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "Links");

            migrationBuilder.DropColumn(
                name: "ApplyLabels",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "FailedLabel",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MaxAttachmentMegabytes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ProcessedLabel",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "AttachmentCount",
                table: "Emails");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Emails");

            migrationBuilder.DropColumn(
                name: "BodyText",
                table: "Emails");

            migrationBuilder.DropColumn(
                name: "LinkCount",
                table: "Emails");

            migrationBuilder.DropColumn(
                name: "ProcessedAt",
                table: "Emails");

            migrationBuilder.DropColumn(
                name: "UidValidity",
                table: "Emails");
        }
    }
}
