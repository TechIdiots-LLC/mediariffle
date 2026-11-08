using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GameControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameControls",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    System = table.Column<string>(type: "TEXT", nullable: false),
                    Layout = table.Column<string>(type: "TEXT", nullable: false),
                    Mapping = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameControls", x => new { x.UserId, x.System });
                    table.ForeignKey(
                        name: "FK_GameControls_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameControls");
        }
    }
}
