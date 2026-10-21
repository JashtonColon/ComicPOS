using ComicPos.Core.Models;
using ComicPos.Data;
using Microsoft.EntityFrameworkCore;

using var db = new ComicDbContext();

// --- existing single Venom seed, unchanged ---
var venom = db.AllSeries.FirstOrDefault(s => s.Name == "Venom");
if (venom == null)
{
    venom = new Series { Name = "Venom", StartYear = 2018 };
    db.AllSeries.Add(venom);
}

var venomIssue = db.Issues
    .Include(i => i.Covers)
    .FirstOrDefault(i => i.Series == venom && i.IssueNumber == 1);
if (venomIssue == null)
{
    venomIssue = new Issue { Series = venom, IssueNumber = 1, Publisher = "MARVEL COMICS" };
    db.Issues.Add(venomIssue);
}

void AddCoverIfMissing(Issue issue, string barcode, string letter, decimal price, int stock)
{
    if (issue.Covers.Any(c => c.Barcode == barcode)) return;
    issue.Covers.Add(new Cover
    {
        Barcode = barcode, CoverLetter = letter, Price = price, StockQuantity = stock
    });
}

AddCoverIfMissing(venomIssue, "111", "A", 4.99m, 3);
AddCoverIfMissing(venomIssue, "222", "B", 4.99m, 2);
AddCoverIfMissing(venomIssue, "333", "D", 4.99m, 4);

db.SaveChanges();
Console.WriteLine("Venom seeded.");

// --- bulk test data: 20 real series, 10 issues each, Cover A/B on every issue ---
var seriesToSeed = new (string Name, int StartYear, string Publisher)[]
{
    ("Amazing Spider-Man", 1963, "MARVEL COMICS"),
    ("X-Men", 1963, "MARVEL COMICS"),
    ("Iron Man", 1968, "MARVEL COMICS"),
    ("Batman", 1940, "DC COMICS"),
    ("Superman", 1939, "DC COMICS"),
    ("Wonder Woman", 1942, "DC COMICS"),
    ("Green Lantern Corps", 1960, "DC COMICS"),
    ("Teen Titans", 1964, "DC COMICS"),
    ("Supergirl", 1959, "DC COMICS"),
    ("Doom Patrol", 1963, "DC COMICS"),
    ("Spawn", 1992, "IMAGE COMICS"),
    ("Radiant Black", 2021, "IMAGE COMICS"),
    ("Transformers", 2023, "IMAGE COMICS"),
    ("G.I. Joe A Real American Hero", 1982, "IMAGE COMICS"),
    ("Blood And Thunder", 2024, "IMAGE COMICS"),
    ("Dungeons And Dragons Total Party Killers", 2025, "DARK HORSE COMICS"),
    ("Star-Crossed", 2025, "DARK HORSE COMICS"),
    ("Star Trek Zero Point", 2026, "IDW PUBLISHING"),
    ("Teenage Mutant Ninja Turtles", 2011, "IDW PUBLISHING"),
    ("Archie Vs. The Terminator", 2026, "DYNAMITE ENTERTAINMENT"),
};

int barcodeCounter = 1000;

foreach (var (name, startYear, publisher) in seriesToSeed)
{
    var series = db.AllSeries.FirstOrDefault(s => s.Name == name);
    if (series == null)
    {
        series = new Series { Name = name, StartYear = startYear };
        db.AllSeries.Add(series);
    }

    for (int issueNum = 1; issueNum <= 10; issueNum++)
    {
        var issue = db.Issues
            .Include(i => i.Covers)
            .FirstOrDefault(i => i.Series == series && i.IssueNumber == issueNum);
        if (issue == null)
        {
            issue = new Issue { Series = series, IssueNumber = issueNum, Publisher = publisher };
            db.Issues.Add(issue);
        }

        var barcodeA = (barcodeCounter++).ToString();
        var barcodeB = (barcodeCounter++).ToString();

        AddCoverIfMissing(issue, barcodeA, "A", 3.99m, 5);
        AddCoverIfMissing(issue, barcodeB, "B", 4.99m, 2);
    }
}

db.SaveChanges();
Console.WriteLine("Bulk test data seeded: 20 series, 10 issues each, Cover A/B.");