using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Games : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GameId",
                table: "UserTags",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GameId",
                table: "CategoryItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    System = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ImagePath = table.Column<string>(type: "TEXT", nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Games_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserTags_GameId",
                table: "UserTags",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryItems_GameId",
                table: "CategoryItems",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_Games_LibraryId_RelativePath",
                table: "Games",
                columns: new[] { "LibraryId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Games_System_Title",
                table: "Games",
                columns: new[] { "System", "Title" });

            migrationBuilder.AddForeignKey(
                name: "FK_CategoryItems_Games_GameId",
                table: "CategoryItems",
                column: "GameId",
                principalTable: "Games",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserTags_Games_GameId",
                table: "UserTags",
                column: "GameId",
                principalTable: "Games",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CategoryItems_Games_GameId",
                table: "CategoryItems");

            migrationBuilder.DropForeignKey(
                name: "FK_UserTags_Games_GameId",
                table: "UserTags");

            migrationBuilder.DropTable(
                name: "Games");

            migrationBuilder.DropIndex(
                name: "IX_UserTags_GameId",
                table: "UserTags");

            migrationBuilder.DropIndex(
                name: "IX_CategoryItems_GameId",
                table: "CategoryItems");

            migrationBuilder.DropColumn(
                name: "GameId",
                table: "UserTags");

            migrationBuilder.DropColumn(
                name: "GameId",
                table: "CategoryItems");
        }
    }
}
