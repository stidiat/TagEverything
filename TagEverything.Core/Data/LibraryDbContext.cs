using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Models;
using TagEverything.Core.Services;

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
                optionsBuilder.UseSqlite(CreateConnectionString(_libraryPath));
            }
        }

        public static string CreateConnectionString(string libraryPath)
        {
            var dataDir = LibraryPaths.Resolve(libraryPath, LibraryPaths.DataDirectory);
            Directory.CreateDirectory(dataDir);

            var currentDbPath = LibraryPaths.Resolve(libraryPath, Path.Combine(LibraryPaths.DataDirectory, "library.db"));
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                LibraryPaths.Internal(libraryPath, "library.db" + suffix);
            if (!File.Exists(currentDbPath))
            {
                var oldDbPath = LibraryPaths.Resolve(libraryPath, "library.db");
                if (File.Exists(oldDbPath)) File.Move(oldDbPath, currentDbPath);
            }

            return new SqliteConnectionStringBuilder
            {
                DataSource = currentDbPath
            }.ToString();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FileRecord>()
                .HasIndex(f => f.Hash);

            modelBuilder.Entity<FileRecord>()
                .HasIndex(f => f.PHash);
                
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
