using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AirGapped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoExportHours",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 24); // hand-edited: the C# default

            migrationBuilder.AddColumn<string>(
                name: "CollectorId",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectorName",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "collector"); // hand-edited: the C# default

            migrationBuilder.AddColumn<int>(
                name: "ExportAiWaitHours",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 6); // hand-edited: the C# default

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Settings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Connected"); // hand-edited: an empty enum string would not load

            migrationBuilder.AddColumn<bool>(
                name: "WatchImportFolder",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ExportedBundles",
                columns: table => new
                {
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Items = table.Column<int>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportedBundles", x => x.Number);
                });

            migrationBuilder.CreateTable(
                name: "ExportedItems",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    BundleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ExportedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportedItems", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "ImportedBundles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    CollectorId = table.Column<string>(type: "TEXT", nullable: true),
                    CollectorName = table.Column<string>(type: "TEXT", nullable: true),
                    Number = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Items = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedBundles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportedItems",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    CollectorId = table.Column<string>(type: "TEXT", nullable: false),
                    BundleNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedItems", x => x.MessageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedBundles_CollectorId_Number",
                table: "ImportedBundles",
                columns: new[] { "CollectorId", "Number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportedBundles");

            migrationBuilder.DropTable(
                name: "ExportedItems");

            migrationBuilder.DropTable(
                name: "ImportedBundles");

            migrationBuilder.DropTable(
                name: "ImportedItems");

            migrationBuilder.DropColumn(
                name: "AutoExportHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "CollectorId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "CollectorName",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ExportAiWaitHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "WatchImportFolder",
                table: "Settings");
        }
    }
}
