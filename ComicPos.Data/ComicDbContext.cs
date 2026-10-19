using Microsoft.EntityFrameworkCore;
using ComicPos.Core.Models;

namespace ComicPos.Data;

public class ComicDbContext : DbContext
{
    public DbSet<Series> AllSeries { get; set; } = null!;
    public DbSet<Issue> Issues { get; set; } = null!;
    public DbSet<Cover> Covers { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(folder, "ComicPos", "comic_pos.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        options.UseSqlite($"Data Source={dbPath}");
    }
}