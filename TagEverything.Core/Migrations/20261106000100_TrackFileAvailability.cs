using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TagEverything.Core.Data;

namespace TagEverything.Core.Migrations;

[DbContext(typeof(LibraryDbContext))]
[Migration("20261106000100_TrackFileAvailability")]
public partial class TrackFileAvailability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("FileIdentity", "Files", "TEXT", nullable: true);
        migrationBuilder.AddColumn<bool>("IsMissing", "Files", "INTEGER", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE Files DROP COLUMN FileIdentity;");
        migrationBuilder.Sql("ALTER TABLE Files DROP COLUMN IsMissing;");
    }
}
