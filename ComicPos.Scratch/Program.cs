using ComicPos.Core.Models;
using ComicPos.Data;
using Microsoft.EntityFrameworkCore;

using var db = new ComicDbContext();

// 1. Create and save
var venom = new Series { Name = "Venom" };
var issue = new Issue
{
    Series = venom,
    IssueNumber = 1,
    Publisher = "MARVEL COMICS"
};
issue.Covers.Add(new Cover { Barcode = "111", CoverLetter = "A", Price = 4.99m, StockQuantity = 3 });
issue.Covers.Add(new Cover { Barcode = "222", CoverLetter = "B", Price = 4.99m, StockQuantity = 2 });

db.Issues.Add(issue);   // saving the issue also saves its series and covers
db.SaveChanges();

// 2. Read back
var loaded = db.Issues
    .Include(i => i.Series)
    .Include(i => i.Covers)
    .First(i => i.IssueNumber == 1);

// 3. Total stock, calculated, never stored
var total = loaded.Covers.Sum(c => c.StockQuantity);
Console.WriteLine($"{loaded.Series.Name} #{loaded.IssueNumber}: {total} total");

foreach (var c in loaded.Covers)
    Console.WriteLine($"  Cover {c.CoverLetter}: {c.StockQuantity}");