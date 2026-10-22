using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AttachmentFilter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedAttachmentKinds",
                table: "Settings",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "Images, Video, Audio, Documents, Archives, Other"); // hand-edited: InboxSettings.DefaultAttachmentKinds, everything but executables

            migrationBuilder.AddColumn<bool>(
                name: "StoredAsZip",
                table: "Attachments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedAttachmentKinds",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "StoredAsZip",
                table: "Attachments");
        }
    }
}
