using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HRMS.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentFileReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileId",
                table: "Documents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileId",
                table: "Documents");
        }
    }
}
