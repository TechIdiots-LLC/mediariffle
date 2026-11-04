using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HomeLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HomeLayout",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HomeLayout",
                table: "AspNetUsers");
        }
    }
}
