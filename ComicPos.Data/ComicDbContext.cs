using Microsoft.EntityFrameworkCore;
using ComicPos.Core.Models;

namespace ComicPos.Data;

public class ComicDbContext : DbContext
{
    public DbSet<Series> AllSeries { get; set; } = null!;
    public DbSet<Issue> Issues { get; set; } = null!;
    public DbSet<Cover> Covers { get; set; } = null!;
    // add a DbSet<Issue> here, named Issues
    // add a DbSet<Cover> here, named Covers

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite("Data Source=comic_pos.db");
        // call options.UseSqlite(...) with "Data Source=comic_pos.db"
    }
}