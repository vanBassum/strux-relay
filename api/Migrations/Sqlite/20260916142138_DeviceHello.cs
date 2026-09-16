using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StruxRelay.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DeviceHello : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Commit",
                table: "Approved",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Hello",
                table: "Approved",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Commit",
                table: "Approved");

            migrationBuilder.DropColumn(
                name: "Hello",
                table: "Approved");
        }
    }
}
