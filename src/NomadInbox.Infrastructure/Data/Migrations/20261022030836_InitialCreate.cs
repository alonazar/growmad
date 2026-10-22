using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cursors",
                columns: table => new
                {
                    Folder = table.Column<string>(type: "TEXT", nullable: false),
                    UidValidity = table.Column<uint>(type: "INTEGER", nullable: false),
                    LastSeenUid = table.Column<uint>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cursors", x => x.Folder);
                });

            migrationBuilder.CreateTable(
                name: "Emails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ImapUid = table.Column<uint>(type: "INTEGER", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", nullable: false),
                    FromAddress = table.Column<string>(type: "TEXT", nullable: false),
                    FromName = table.Column<string>(type: "TEXT", nullable: true),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Fields = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Emails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ImapHost = table.Column<string>(type: "TEXT", nullable: false),
                    ImapPort = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPassword = table.Column<string>(type: "TEXT", nullable: true),
                    Folder = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectPrefix = table.Column<string>(type: "TEXT", nullable: false),
                    AllowedSenders = table.Column<string>(type: "TEXT", nullable: false),
                    ImportSince = table.Column<long>(type: "INTEGER", nullable: true),
                    PollingEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Emails_MessageId",
                table: "Emails",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Emails_ReceivedAt",
                table: "Emails",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Emails_Status",
                table: "Emails",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cursors");

            migrationBuilder.DropTable(
                name: "Emails");

            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
