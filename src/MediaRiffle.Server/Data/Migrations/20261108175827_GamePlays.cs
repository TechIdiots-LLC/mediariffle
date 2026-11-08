using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GamePlays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GamePlays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    System = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    LastPlayedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SaveSize = table.Column<long>(type: "INTEGER", nullable: false),
                    SaveUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SaveHash = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamePlays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GamePlays_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GamePlays_UserId_LastPlayedUtc",
                table: "GamePlays",
                columns: new[] { "UserId", "LastPlayedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GamePlays_UserId_System_FileName",
                table: "GamePlays",
                columns: new[] { "UserId", "System", "FileName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GamePlays");
        }
    }
}
