using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Appearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ColorMode",
                table: "Settings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "System"); // hand-edited: the existing settings row gets the C# default (an empty enum string would not load)

            migrationBuilder.AddColumn<string>(
                name: "Skin",
                table: "Settings",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "classic"); // hand-edited: SkinCatalog.DefaultId
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ColorMode",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Skin",
                table: "Settings");
        }
    }
}
