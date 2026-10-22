using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SavedPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxPageAssetMegabytes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 25); // hand-edited default

            migrationBuilder.AddColumn<int>(
                name: "MaxPageMegabytes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 100); // hand-edited default

            migrationBuilder.AddColumn<string>(
                name: "PageArchiveMode",
                table: "Settings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Tagged"); // hand-edited default

            migrationBuilder.AddColumn<string>(
                name: "PageStylesheet",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageTemplate",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SaveLinkedPagesLimit",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 25); // hand-edited default

            migrationBuilder.CreateTable(
                name: "Pages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    FinalUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    SiteName = table.Column<string>(type: "TEXT", nullable: true),
                    Byline = table.Column<string>(type: "TEXT", nullable: true),
                    Excerpt = table.Column<string>(type: "TEXT", nullable: true),
                    TextContent = table.Column<string>(type: "TEXT", nullable: true),
                    ContentPath = table.Column<string>(type: "TEXT", nullable: true),
                    InboxEmailId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceLinkId = table.Column<int>(type: "INTEGER", nullable: true),
                    SavedFromPageId = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ArchivedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PageAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArchivedPageId = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PageAssets_Pages_ArchivedPageId",
                        column: x => x.ArchivedPageId,
                        principalTable: "Pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PageLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArchivedPageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PageLinks_Pages_ArchivedPageId",
                        column: x => x.ArchivedPageId,
                        principalTable: "Pages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PageAssets_ArchivedPageId",
                table: "PageAssets",
                column: "ArchivedPageId");

            migrationBuilder.CreateIndex(
                name: "IX_PageAssets_FileName",
                table: "PageAssets",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_PageLinks_ArchivedPageId",
                table: "PageLinks",
                column: "ArchivedPageId");

            migrationBuilder.CreateIndex(
                name: "IX_PageLinks_Url",
                table: "PageLinks",
                column: "Url");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_InboxEmailId",
                table: "Pages",
                column: "InboxEmailId");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_Status",
                table: "Pages",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_Url",
                table: "Pages",
                column: "Url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageAssets");

            migrationBuilder.DropTable(
                name: "PageLinks");

            migrationBuilder.DropTable(
                name: "Pages");

            migrationBuilder.DropColumn(
                name: "MaxPageAssetMegabytes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MaxPageMegabytes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PageArchiveMode",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PageStylesheet",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PageTemplate",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SaveLinkedPagesLimit",
                table: "Settings");
        }
    }
}
