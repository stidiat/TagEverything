using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;
using TagEverything.Core.Models;
using TagEverything.Core.Services;
using Xunit;

namespace TagEverything.Tests
{
    public class CoreServicesTests
    {
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
                var keyword = "#holiday";
                var searchWord = keyword.Substring(1);
                query = query.Where(f => f.Tags.Any(t => EF.Functions.Like(t.Name, $"%{searchWord}%")));

                var results = await query.ToListAsync();

                // Assert
                Assert.Single(results);
                Assert.Equal("vacation.mp4", results.First().OriginalName);
            }
            
            connection.Close();
        }
    }
}
