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

// --- bulk test data: 50 fake customers ---
var firstNames = new[]
{
    "James", "Mary", "Robert", "Patricia", "John", "Jennifer", "Michael", "Linda",
    "David", "Elizabeth", "William", "Barbara", "Richard", "Susan", "Joseph", "Jessica",
    "Thomas", "Sarah", "Charles", "Karen", "Daniel", "Nancy", "Matthew", "Lisa",
    "Anthony", "Betty"
};

var lastNames = new[]
{
    "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis",
    "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson",
    "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson",
    "White", "Harris"
};

var rng = new Random(42); // fixed seed so the same 50 names generate every run

for (int i = 0; i < 50; i++)
{
    var first = firstNames[rng.Next(firstNames.Length)];
    var last = lastNames[rng.Next(lastNames.Length)];
    var name = $"{first} {last}";

    // Skip if a customer with this exact name already exists, same
    // get-or-create discipline as the series seeding above.
    if (db.Customers.Any(c => c.Name == name)) continue;

    // Vary email/phone presence: ~70% have an email, ~50% have a phone,
    // some have neither, exercising the nullable fields for real.
    string? email = rng.NextDouble() < 0.7
        ? $"{first.ToLower()}.{last.ToLower()}{i}@example.com"
        : null;

    string? phone = rng.NextDouble() < 0.5
        ? $"555-{rng.Next(1000, 9999)}"
        : null;

    db.Customers.Add(new Customer
    {
        Name = name,
        Email = email,
        Phone = phone,
        IsActive = true
    });
}

// --- one-off cleanup: remove junk "wwww..." test customers ---
// --- one-off cleanup: remove junk "wwww..." test customers ---
var allCustomers = db.Customers.ToList(); // pull into memory first

var junkCustomers = allCustomers
    .Where(c => c.Name.All(ch => ch == 'w' || ch == 'W'))
    .ToList();

if (junkCustomers.Count > 0)
{
    db.Customers.RemoveRange(junkCustomers);
    db.SaveChanges();
    Console.WriteLine($"Removed {junkCustomers.Count} junk customer(s).");
}
else
{
    Console.WriteLine("No junk customers found.");
}
db.SaveChanges();
Console.WriteLine("Bulk test data seeded: 20 series, 10 issues each, Cover A/B.");
// --- bulk test data: fake sales history, tied to real customers/covers ---
// Guarded by a count threshold instead of "any sales at all", because real
// sales from manual Checkout testing already exist in the database before
// this ever runs. 100+ sales means the fake batch already ran once.
if (db.Sales.Count() < 100)
{
    var allCoversForSales = db.Covers
        .Include(c => c.Issue).ThenInclude(i => i.Series)
        .ToList();

    var allCustomersForSales = db.Customers.ToList();

    var salesRng = new Random(7); // fixed seed, reproducible fake history

    for (int s = 0; s < 250; s++)
    {
        // ~80% of sales are tied to a known customer, ~20% are walk-ins (no customer)
        int? customerId = salesRng.NextDouble() < 0.8
            ? allCustomersForSales[salesRng.Next(allCustomersForSales.Count)].Id
            : null;

        var sale = new Sale
        {
            CustomerId = customerId,
            SoldAt = DateTime.Now.AddDays(-salesRng.Next(0, 120)).AddHours(-salesRng.Next(0, 10)),
        };

        int lineCount = salesRng.Next(1, 4); // 1-3 different comics per sale
        for (int l = 0; l < lineCount; l++)
        {
            var cover = allCoversForSales[salesRng.Next(allCoversForSales.Count)];
            var qty = salesRng.Next(1, 3);
            var discount = salesRng.NextDouble() < 0.15 ? 10m : 0m; // occasional 10% discount

            sale.Lines.Add(new SaleLine
            {
                Cover = cover,
                CoverId = cover.Id,
                Quantity = qty,
                PriceEach = cover.Price,
                DiscountPercent = discount
            });
        }

        db.Sales.Add(sale);
    }

    db.SaveChanges();
    Console.WriteLine("Seeded 250 fake sales across existing customers and inventory.");
}
else
{
    Console.WriteLine("Sales already exist, skipping fake sales seed.");
}

// --- bulk test data: fake pull list subscriptions, so the FOC report has something to show ---
// Guarded by a count threshold, same reasoning as the fake sales block above.
if (db.PullListEntries.Count() < 20)
{
    var allSeriesForPullLists = db.AllSeries.ToList();
    var allCustomersForPullLists = db.Customers.ToList();
    var pullRng = new Random(13);

    int added = 0;
    for (int i = 0; i < 60 && added < 60; i++)
    {
        var customer = allCustomersForPullLists[pullRng.Next(allCustomersForPullLists.Count)];
        var series = allSeriesForPullLists[pullRng.Next(allSeriesForPullLists.Count)];

        bool alreadyOnList = db.PullListEntries.Local
            .Any(p => p.CustomerId == customer.Id && p.SeriesId == series.Id)
            || db.PullListEntries.Any(p => p.CustomerId == customer.Id && p.SeriesId == series.Id);
        if (alreadyOnList) continue;

        var coverLetter = pullRng.NextDouble() < 0.85 ? "A" : "B";

        db.PullListEntries.Add(new PullListEntry
        {
            CustomerId = customer.Id,
            SeriesId = series.Id,
            PreferredCoverLetter = coverLetter,
            IsActive = true
        });
        added++;
    }

    db.SaveChanges();
    Console.WriteLine($"Seeded {added} fake pull list subscriptions.");
}
else
{
    Console.WriteLine("Pull list entries already exist, skipping fake pull list seed.");
}
