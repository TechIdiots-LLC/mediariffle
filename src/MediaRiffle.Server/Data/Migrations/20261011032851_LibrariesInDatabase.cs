using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibrariesInDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Libraries move from the settings file into the database, and the media points at them by id instead
            // of by name. What was scanned is rebuilt by the next scan, so it's cleared rather than converted (as
            // are watch positions, which point at it). Accounts and invites are kept.
            migrationBuilder.Sql("DELETE FROM Progress; DELETE FROM MediaFiles; DELETE FROM Seasons; DELETE FROM Shows; DELETE FROM Movies;");

            migrationBuilder.DropIndex(
                name: "IX_Shows_Library_Folder",
                table: "Shows");

            migrationBuilder.DropIndex(
                name: "IX_Movies_Library_GroupKey",
                table: "Movies");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_Library_RelativePath",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Library",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "Library",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "Library",
                table: "MediaFiles");

            migrationBuilder.AddColumn<int>(
                name: "LibraryId",
                table: "Shows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LibraryId",
                table: "Movies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LibraryId",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Libraries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Libraries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Shows_LibraryId_Folder",
                table: "Shows",
                columns: new[] { "LibraryId", "Folder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movies_LibraryId_GroupKey",
                table: "Movies",
                columns: new[] { "LibraryId", "GroupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_LibraryId_RelativePath",
                table: "MediaFiles",
                columns: new[] { "LibraryId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_Name",
                table: "Libraries",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaFiles_Libraries_LibraryId",
                table: "MediaFiles",
                column: "LibraryId",
                principalTable: "Libraries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Movies_Libraries_LibraryId",
                table: "Movies",
                column: "LibraryId",
                principalTable: "Libraries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Shows_Libraries_LibraryId",
                table: "Shows",
                column: "LibraryId",
                principalTable: "Libraries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaFiles_Libraries_LibraryId",
                table: "MediaFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_Movies_Libraries_LibraryId",
                table: "Movies");

            migrationBuilder.DropForeignKey(
                name: "FK_Shows_Libraries_LibraryId",
                table: "Shows");

            migrationBuilder.DropTable(
                name: "Libraries");

            migrationBuilder.DropIndex(
                name: "IX_Shows_LibraryId_Folder",
                table: "Shows");

            migrationBuilder.DropIndex(
                name: "IX_Movies_LibraryId_GroupKey",
                table: "Movies");

            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_LibraryId_RelativePath",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "LibraryId",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "LibraryId",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "LibraryId",
                table: "MediaFiles");

            migrationBuilder.AddColumn<string>(
                name: "Library",
                table: "Shows",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Library",
                table: "Movies",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Library",
                table: "MediaFiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Shows_Library_Folder",
                table: "Shows",
                columns: new[] { "Library", "Folder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movies_Library_GroupKey",
                table: "Movies",
                columns: new[] { "Library", "GroupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Library_RelativePath",
                table: "MediaFiles",
                columns: new[] { "Library", "RelativePath" },
                unique: true);
        }
    }
}
