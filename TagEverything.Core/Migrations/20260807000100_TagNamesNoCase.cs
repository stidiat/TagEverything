using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TagEverything.Core.Data;

#nullable disable

namespace TagEverything.Core.Migrations
{
    [DbContext(typeof(LibraryDbContext))]
    [Migration("20260807000100_TagNamesNoCase")]
    public partial class TagNamesNoCase : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Tags_Name;");

            // Preserve every file/tag association before removing tags that differ
            // only by ASCII casing (the same semantics as SQLite NOCASE).
            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO FileTags (FilesId, TagsId)
                SELECT ft.FilesId, canonical.CanonicalId
                FROM FileTags AS ft
                INNER JOIN Tags AS t ON t.Id = ft.TagsId
                INNER JOIN (
                    SELECT lower(Name) AS NormalizedName, MIN(Id) AS CanonicalId
                    FROM Tags
                    GROUP BY lower(Name)
                ) AS canonical ON lower(t.Name) = canonical.NormalizedName
                WHERE ft.TagsId <> canonical.CanonicalId;
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM FileTags
                WHERE TagsId NOT IN (
                    SELECT MIN(Id)
                    FROM Tags
                    GROUP BY lower(Name)
                );
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM Tags
                WHERE Id NOT IN (
                    SELECT MIN(Id)
                    FROM Tags
                    GROUP BY lower(Name)
                );
                """);

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IX_Tags_Name ON Tags (Name COLLATE NOCASE);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Tags_Name;");
            migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Tags_Name ON Tags (Name);");
        }
    }
}
