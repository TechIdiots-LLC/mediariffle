using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubtitleLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubtitleLanguage",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubtitleLanguage",
                table: "AspNetUsers");
        }
    }
}
