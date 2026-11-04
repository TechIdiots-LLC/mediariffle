using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CategoryItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CategoryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "NOCASE"),
                    ShowId = table.Column<int>(type: "INTEGER", nullable: true),
                    MovieId = table.Column<int>(type: "INTEGER", nullable: true),
                    ArtistId = table.Column<int>(type: "INTEGER", nullable: true),
                    AlbumId = table.Column<int>(type: "INTEGER", nullable: true),
                    MediaFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryItems_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryItems_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryItems_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryItems_Movies_MovieId",
                        column: x => x.MovieId,
                        principalTable: "Movies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CategoryItems_Shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "Shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryItems_AlbumId",
                table: "CategoryItems",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryItems_ArtistId",
                table: "CategoryItems",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryItems_MediaFileId",
                table: "CategoryItems",
                column: "MediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryItems_MovieId",
                table: "CategoryItems",
                column: "MovieId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryItems_ShowId",
                table: "CategoryItems",
                column: "ShowId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryItems_Tag",
                table: "CategoryItems",
                column: "Tag");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryItems");
        }
    }
}
