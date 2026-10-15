using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class MusicListsAndHidden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AlbumId",
                table: "UserTags",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArtistId",
                table: "UserTags",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaFileId",
                table: "UserTags",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlbumId",
                table: "Hidden",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArtistId",
                table: "Hidden",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTags_AlbumId",
                table: "UserTags",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTags_ArtistId",
                table: "UserTags",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_UserTags_MediaFileId",
                table: "UserTags",
                column: "MediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Hidden_AlbumId",
                table: "Hidden",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_Hidden_ArtistId",
                table: "Hidden",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Hidden_UserId_AlbumId",
                table: "Hidden",
                columns: new[] { "UserId", "AlbumId" });

            migrationBuilder.CreateIndex(
                name: "IX_Hidden_UserId_ArtistId",
                table: "Hidden",
                columns: new[] { "UserId", "ArtistId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Hidden_Albums_AlbumId",
                table: "Hidden",
                column: "AlbumId",
                principalTable: "Albums",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Hidden_Artists_ArtistId",
                table: "Hidden",
                column: "ArtistId",
                principalTable: "Artists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserTags_Albums_AlbumId",
                table: "UserTags",
                column: "AlbumId",
                principalTable: "Albums",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserTags_Artists_ArtistId",
                table: "UserTags",
                column: "ArtistId",
                principalTable: "Artists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserTags_MediaFiles_MediaFileId",
                table: "UserTags",
                column: "MediaFileId",
                principalTable: "MediaFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Hidden_Albums_AlbumId",
                table: "Hidden");

            migrationBuilder.DropForeignKey(
                name: "FK_Hidden_Artists_ArtistId",
                table: "Hidden");

            migrationBuilder.DropForeignKey(
                name: "FK_UserTags_Albums_AlbumId",
                table: "UserTags");

            migrationBuilder.DropForeignKey(
                name: "FK_UserTags_Artists_ArtistId",
                table: "UserTags");

            migrationBuilder.DropForeignKey(
                name: "FK_UserTags_MediaFiles_MediaFileId",
                table: "UserTags");

            migrationBuilder.DropIndex(
                name: "IX_UserTags_AlbumId",
                table: "UserTags");

            migrationBuilder.DropIndex(
                name: "IX_UserTags_ArtistId",
                table: "UserTags");

            migrationBuilder.DropIndex(
                name: "IX_UserTags_MediaFileId",
                table: "UserTags");

            migrationBuilder.DropIndex(
                name: "IX_Hidden_AlbumId",
                table: "Hidden");

            migrationBuilder.DropIndex(
                name: "IX_Hidden_ArtistId",
                table: "Hidden");

            migrationBuilder.DropIndex(
                name: "IX_Hidden_UserId_AlbumId",
                table: "Hidden");

            migrationBuilder.DropIndex(
                name: "IX_Hidden_UserId_ArtistId",
                table: "Hidden");

            migrationBuilder.DropColumn(
                name: "AlbumId",
                table: "UserTags");

            migrationBuilder.DropColumn(
                name: "ArtistId",
                table: "UserTags");

            migrationBuilder.DropColumn(
                name: "MediaFileId",
                table: "UserTags");

            migrationBuilder.DropColumn(
                name: "AlbumId",
                table: "Hidden");

            migrationBuilder.DropColumn(
                name: "ArtistId",
                table: "Hidden");
        }
    }
}
