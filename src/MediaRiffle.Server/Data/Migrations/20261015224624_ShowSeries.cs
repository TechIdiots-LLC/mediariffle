using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShowSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Seasons_ShowId_Number",
                table: "Seasons");

            migrationBuilder.AddColumn<string>(
                name: "Series",
                table: "Seasons",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Seasons_ShowId_Series_Number",
                table: "Seasons",
                columns: new[] { "ShowId", "Series", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Seasons_ShowId_Series_Number",
                table: "Seasons");

            migrationBuilder.DropColumn(
                name: "Series",
                table: "Seasons");

            migrationBuilder.CreateIndex(
                name: "IX_Seasons_ShowId_Number",
                table: "Seasons",
                columns: new[] { "ShowId", "Number" },
                unique: true);
        }
    }
}
