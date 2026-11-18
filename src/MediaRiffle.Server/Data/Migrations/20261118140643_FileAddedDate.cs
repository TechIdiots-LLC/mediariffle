using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaRiffle.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class FileAddedDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AddedUtc",
                table: "MediaFiles",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
            // Files from before: the earlier of when a scan last read it and the file's own time (the first scan read
            // everything at once, so for those the file's time tells them apart)
            migrationBuilder.Sql("UPDATE MediaFiles SET AddedUtc = MIN(ModifiedUtc, ScannedUtc);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedUtc",
                table: "MediaFiles");
        }
    }
}
