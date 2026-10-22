using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenderJavaScriptPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RenderJavaScriptPages",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // hand-edited: on for the existing settings row, like the C# default
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RenderJavaScriptPages",
                table: "Settings");
        }
    }
}
