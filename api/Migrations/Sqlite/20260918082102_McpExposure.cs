using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StruxRelay.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class McpExposure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "McpExposed",
                table: "Approved",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "McpExposed",
                table: "Approved");
        }
    }
}
