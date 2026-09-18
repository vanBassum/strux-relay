using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StruxRelay.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class McpOAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientName",
                table: "McpTokens",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "McpTokens",
                type: "TEXT",
                nullable: true);

            // "issued" and not "": every row that exists when this runs was created on
            // the dashboard, which is exactly what that value means. An empty string
            // would be a third state the model does not have.
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "McpTokens",
                type: "TEXT",
                nullable: false,
                defaultValue: "issued");

            migrationBuilder.Sql("UPDATE McpTokens SET Kind = 'issued' WHERE Kind = ''");

            migrationBuilder.AddColumn<string>(
                name: "RefreshHash",
                table: "McpTokens",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Resource",
                table: "McpTokens",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "McpAuthCodes",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: false),
                    RedirectUri = table.Column<string>(type: "TEXT", nullable: false),
                    CodeChallenge = table.Column<string>(type: "TEXT", nullable: false),
                    Resource = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAuthCodes", x => x.Hash);
                });

            migrationBuilder.CreateTable(
                name: "McpOAuthClients",
                columns: table => new
                {
                    ClientId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    RedirectUris = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOAuthClients", x => x.ClientId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpAuthCodes");

            migrationBuilder.DropTable(
                name: "McpOAuthClients");

            migrationBuilder.DropColumn(
                name: "ClientName",
                table: "McpTokens");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "McpTokens");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "McpTokens");

            migrationBuilder.DropColumn(
                name: "RefreshHash",
                table: "McpTokens");

            migrationBuilder.DropColumn(
                name: "Resource",
                table: "McpTokens");
        }
    }
}
