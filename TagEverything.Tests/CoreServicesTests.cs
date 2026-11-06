using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TagEverything.Core.Data;
using TagEverything.Core.Models;
using TagEverything.Core.Services;
using Xunit;

namespace TagEverything.Tests
{
    public class CoreServicesTests
    {
        [Theory]
        [InlineData("photo.JPG", "JPG")]
        [InlineData("archive.tar.gz", "GZ")]
        [InlineData("README", "无扩展名")]
        [InlineData("视频.MP4", "MP4")]
        public void FileRecord_ShouldDisplayFileType(string name, string expected)
        {
            Assert.Equal(expected, new FileRecord { OriginalName = name }.FileType);
        }

        [Fact]
        public async Task HashService_ShouldReturnCorrectSha256()
        {
            // Arrange
            var hashService = new HashService();
            var testFilePath = Path.GetTempFileName();
            File.WriteAllText(testFilePath, "Hello World");

            // Act
            var hash = await hashService.CalculateSha256Async(testFilePath);

            // Assert
            Assert.Equal("a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e", hash);

            // Cleanup
            File.Delete(testFilePath);
        }

        [Fact]
        public void MetadataExtractionService_ShouldIdentifyVideoExtensions()
        {
            // Arrange
            var service = new MetadataExtractionService();

            // Act & Assert
            Assert.True(service.IsVideoFile(".mp4"));
            Assert.True(service.IsVideoFile(".MKV"));
            Assert.False(service.IsVideoFile(".txt"));
        }

        [Fact]
        public async Task SearchAlgorithm_ShouldFilterCorrectly()
        {
            // Arrange
            var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<LibraryDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var context = new LibraryDbContext(options))
            {
                await context.Database.EnsureCreatedAsync();

                var file1 = new FileRecord { OriginalName = "vacation.mp4" };
                var file2 = new FileRecord { OriginalName = "work.docx" };
                
                var tag1 = new Tag { Name = "holiday" };
                var tag2 = new Tag { Name = "document" };

                file1.Tags.Add(tag1);
                file2.Tags.Add(tag2);

                context.Files.Add(file1);
                context.Files.Add(file2);
                await context.SaveChangesAsync();
            }

            // Act
            using (var context = new LibraryDbContext(options))
            {
                var query = context.Files.Include(f => f.Tags).AsQueryable();
                
                // Simulate search query: "#holiday"
                query = FileSearchService.ApplyFilters(query, "#holiday", Array.Empty<string>());

                var results = await query.ToListAsync();

                // Assert
                Assert.Single(results);
                Assert.Equal("vacation.mp4", results.First().OriginalName);
            }
            
            connection.Close();
        }

        [Fact]
        public async Task DuplicateDetection_ShouldOnlyTreatMatchingHashAsExact()
        {
            var testRoot = CreateTestDirectory();
            try
            {
                await using var context = new LibraryDbContext(testRoot);
                await context.Database.MigrateAsync();
                context.Files.AddRange(
                    new FileRecord { OriginalName = "one.bin", Hash = "hash-one", Size = 42, PHash = 0xf0 },
                    new FileRecord { OriginalName = "two.bin", Hash = "hash-two", Size = 42 });
                await context.SaveChangesAsync();

                var service = new DuplicateDetectionService();
                var sameSizeOnly = await service.FindDuplicateAsync(context, "different-hash", 0);
                var sameHash = await service.FindDuplicateAsync(context, "hash-one", 0);
                var visuallySimilar = await service.FindDuplicateAsync(context, "third-hash", 0xf1);

                Assert.Null(sameSizeOnly.File);
                Assert.NotNull(sameHash.File);
                Assert.True(sameHash.IsExact);
                Assert.Equal("one.bin", sameHash.File!.OriginalName);
                Assert.NotNull(visuallySimilar.File);
                Assert.False(visuallySimilar.IsExact);
                Assert.Equal(1, visuallySimilar.HammingDistance);
            }
            finally
            {
                DeleteTestDirectory(testRoot);
            }
        }

        [Fact]
        public void FileManagement_RejectsPathsOutsideLibrary()
        {
            var testRoot = CreateTestDirectory();
            try
            {
                var libraryPath = Path.Combine(testRoot, "library");
                var service = new FileManagementService(libraryPath);
                Assert.Equal(Path.Combine(libraryPath, "sample.txt"), service.GetLibraryFilePath("sample.txt"));
                Assert.Throws<InvalidOperationException>(() =>
                    service.GetLibraryFilePath(Path.Combine("..", "outside.txt")));
            }
            finally
            {
                DeleteTestDirectory(testRoot);
            }
        }

        [Fact]
        public async Task Database_ShouldStoreFullUnsignedPHashRange()
        {
            var testRoot = CreateTestDirectory();
            try
            {
                await using (var context = new LibraryDbContext(testRoot))
                {
                    await context.Database.MigrateAsync();
                    context.Files.Add(new FileRecord
                    {
                        OriginalName = "high-bit.png",
                        Hash = "p-hash-test",
                        PHash = ulong.MaxValue
                    });
                    await context.SaveChangesAsync();
                }

                await using var verificationContext = new LibraryDbContext(testRoot);
                var record = await verificationContext.Files.SingleAsync();
                Assert.Equal(ulong.MaxValue, record.PHash);
            }
            finally
            {
                DeleteTestDirectory(testRoot);
            }
        }

        [Fact]
        public async Task TagNames_ShouldBeUniqueIgnoringCase()
        {
            var testRoot = CreateTestDirectory();
            try
            {
                await using (var context = new LibraryDbContext(testRoot))
                {
                    await context.Database.MigrateAsync();
                    context.Tags.Add(new Tag { Name = "Holiday" });
                    await context.SaveChangesAsync();
                }

                await using var duplicateContext = new LibraryDbContext(testRoot);
                duplicateContext.Tags.Add(new Tag { Name = "holiday" });
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    duplicateContext.SaveChangesAsync());
            }
            finally
            {
                DeleteTestDirectory(testRoot);
            }
        }

        [Fact]
        public async Task TagMigration_ShouldMergeExistingCaseVariantsWithoutLosingLinks()
        {
            var testRoot = CreateTestDirectory();
            try
            {
                await using (var oldSchemaContext = new LibraryDbContext(testRoot))
                {
                    var migrator = oldSchemaContext.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                    await migrator.MigrateAsync("20260324015838_AddPHash");

                    await oldSchemaContext.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO Files (Id, OriginalName, SavedName, RelativePath, Size, Hash, PHash, IsVideo,
                                           AddedAt, FileCreationTime, FileModificationTime, MetadataJson)
                        VALUES (1, 'one.jpg', 'one.jpg', 'one.jpg', 0, 'case-one', 0, 0, '2026-03-24', '2026-03-24', '2026-03-24', ''),
                               (2, 'two.jpg', 'two.jpg', 'two.jpg', 0, 'case-two', 0, 0, '2026-03-24', '2026-03-24', '2026-03-24', '');
                        INSERT INTO Tags (Id, Name) VALUES (1, 'Holiday'), (2, 'holiday');
                        INSERT INTO FileTags (FilesId, TagsId) VALUES (1, 1), (2, 2);
                        """);
                }

                await using var upgradedContext = new LibraryDbContext(testRoot);
                await upgradedContext.Database.MigrateAsync();
                var tags = await upgradedContext.Tags.Include(t => t.Files).ToListAsync();

                var mergedTag = Assert.Single(tags);
                Assert.Equal(2, mergedTag.Files.Count);
                Assert.All(mergedTag.Files, file =>
                {
                    Assert.False(file.IsMissing);
                    Assert.Null(file.FileIdentity);
                });
            }
            finally
            {
                DeleteTestDirectory(testRoot);
            }
        }

        private static string CreateTestDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "TagEverythingTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTestDirectory(string path)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(path, true);
        }
    }
}
