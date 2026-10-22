using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KnowledgeBaseEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // hand-edited default

            migrationBuilder.AddColumn<bool>(
                name: "KnowledgeCollectionPerCategory",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // hand-edited default

            migrationBuilder.AddColumn<string>(
                name: "KnowledgeDefaultCollection",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "nomad inbox"); // hand-edited default

            migrationBuilder.AddColumn<bool>(
                name: "KnowledgeIncludeFullText",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // hand-edited default

            migrationBuilder.AddColumn<string>(
                name: "NomadUrl",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "http://host.docker.internal:8080"); // hand-edited default

            migrationBuilder.CreateTable(
                name: "DeletedMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletedMessages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemKey = table.Column<string>(type: "TEXT", nullable: false),
                    InboxEmailId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Collection = table.Column<string>(type: "TEXT", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    NomadFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    NomadSource = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Chunks = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    UploadedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_InboxEmailId",
                table: "KnowledgeDocuments",
                column: "InboxEmailId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_ItemKey",
                table: "KnowledgeDocuments",
                column: "ItemKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeletedMessages");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "KnowledgeBaseEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "KnowledgeCollectionPerCategory",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "KnowledgeDefaultCollection",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "KnowledgeIncludeFullText",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "NomadUrl",
                table: "Settings");
        }
    }
}
