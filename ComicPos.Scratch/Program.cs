using ComicPos.Core.Models;
using ComicPos.Data;
using Microsoft.EntityFrameworkCore;

using var db = new ComicDbContext();

var venom = db.AllSeries.FirstOrDefault(s => s.Name == "Venom");
if (venom == null)
{
    venom = new Series { Name = "Venom" };
    db.AllSeries.Add(venom);
}

var issue = db.Issues
    .Include(i => i.Covers)
    .FirstOrDefault(i => i.Series == venom && i.IssueNumber == 1);
if (issue == null)
{
    issue = new Issue { Series = venom, IssueNumber = 1, Publisher = "MARVEL COMICS" };
    db.Issues.Add(issue);
}

void AddCoverIfMissing(string barcode, string letter, decimal price, int stock)
{
    if (issue.Covers.Any(c => c.Barcode == barcode)) return;
    issue.Covers.Add(new Cover
    {
        Barcode = barcode, CoverLetter = letter, Price = price, StockQuantity = stock
    });
}

AddCoverIfMissing("111", "A", 4.99m, 3);
AddCoverIfMissing("222", "B", 4.99m, 2);
AddCoverIfMissing("333", "D", 4.99m, 4);

db.SaveChanges();

Console.WriteLine("Seeded.");