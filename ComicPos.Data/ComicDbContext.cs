using Microsoft.EntityFrameworkCore;
using ComicPos.Core.Models;

namespace ComicPos.Data;

public class ComicDbContext : DbContext
{
    public DbSet<Series> AllSeries { get; set; } = null!;
    public DbSet<Issue> Issues { get; set; } = null!;
    public DbSet<Cover> Covers { get; set; } = null!;
    public DbSet<Sale> Sales { get; set; } = null!;
    public DbSet<SaleLine> SaleLines { get; set; } = null!;
    public DbSet<Customer> Customers { get; set; } = null!;
    public DbSet<PullListEntry> PullListEntries { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(folder, "ComicPos", "comic_pos.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        options.UseSqlite($"Data Source={dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Cover>()
            .HasIndex(c => c.Barcode)
            .IsUnique();
    }
}