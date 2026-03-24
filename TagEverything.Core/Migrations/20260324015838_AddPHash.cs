using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TagEverything.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "PHash",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.CreateIndex(
                name: "IX_Files_PHash",
                table: "Files",
                column: "PHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_PHash",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PHash",
                table: "Files");
        }
    }
}
