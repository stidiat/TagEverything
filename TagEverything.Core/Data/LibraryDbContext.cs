using System.IO;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Models;

namespace TagEverything.Core.Data
{
    public class LibraryDbContext : DbContext
    {
        public DbSet<FileRecord> Files { get; set; } = null!;
        public DbSet<Tag> Tags { get; set; } = null!;

        private readonly string? _libraryPath;

        public LibraryDbContext(string libraryPath)
        {
            _libraryPath = libraryPath;
        }

        public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured && _libraryPath != null)
            {
                var dataDir = Path.Combine(_libraryPath, "TagEverythingData");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                
                var oldDbPath = Path.Combine(_libraryPath, "library.db");
                var currentDbPath = Path.Combine(dataDir, "library.db");
                if (File.Exists(oldDbPath) && !File.Exists(currentDbPath))
                {
                    File.Move(oldDbPath, currentDbPath);
                }
                
                optionsBuilder.UseSqlite($"Data Source={currentDbPath}");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FileRecord>()
                .HasIndex(f => f.Hash);
                
            modelBuilder.Entity<Tag>()
                .HasIndex(t => t.Name)
                .IsUnique();

            modelBuilder.Entity<FileRecord>()
                .HasMany(f => f.Tags)
                .WithMany(t => t.Files)
                .UsingEntity(j => j.ToTable("FileTags"));
        }
    }
}
