using ComicPos.Core;
using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class InventoryService : IInventoryService
{
    private readonly ComicDbContext _db;

    public InventoryService(ComicDbContext db)
    {
        _db = db;
    }

    public Cover? FindByBarcode(string barcode)
    {
        return _db.Covers
            .Include(c => c.Issue)
            .ThenInclude(i => i.Series)
            .FirstOrDefault(c => c.Barcode == barcode);
    }

    public List<Issue> SearchIssues(string text)
    {
        return _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .Where(i => EF.Functions.Like(i.Series.Name, $"%{text}%"))
            .ToList();
    }

    public List<string> GetAllSeriesNames()
    {
        return _db.AllSeries
            .Select(s => s.Name)
            .ToList();
    }
    public List<Issue> GetIssuesForSeries(string seriesName, IssueType type)
    {
        return _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .Where(i => i.Series.Name == seriesName && i.Type == type)
            .ToList();
    }

    public List<Series> GetAllSeriesForDisplay()
    {
        return _db.AllSeries.ToList();
    }
    public List<Series> SearchSeriesForDisplay(string text)
    {
        return _db.AllSeries
            .Where(s => EF.Functions.Like(s.Name, $"%{text}%"))
            .ToList();
    }

    // See IInventoryService's own doc comment for why these two exist
    // separately from the pair just above (Inventory page only, never the
    // Customers pull-list subscribe picker) and what "received" means here.
    public List<Series> SearchReceivedSeriesForDisplay(string text)
    {
        return _db.AllSeries
            .Where(s => EF.Functions.Like(s.Name, $"%{text}%")
                && s.Issues.Any(i => i.Covers.Any(c => c.StockQuantity > 0)))
            .ToList();
    }

    public List<Issue> GetReceivedIssuesForSeries(string seriesName, IssueType type)
    {
        return _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .Where(i => i.Series.Name == seriesName && i.Type == type
                && i.Covers.Any(c => c.StockQuantity > 0))
            .ToList();
    }

    public Series? GetSeriesByName(string name)
    {
        return _db.AllSeries.FirstOrDefault(s => s.Name == name);
    }

    public List<string> GetKnownCoverLetters(int seriesId)
    {
        return _db.Issues
            .Include(i => i.Covers)
            .Where(i => i.SeriesId == seriesId)
            .SelectMany(i => i.Covers.Select(c => c.CoverLetter))
            .Where(letter => !string.IsNullOrWhiteSpace(letter))
            .Distinct()
            .OrderBy(letter => letter)
            .ToList();
    }

    // Used when receiving a shipment (stock goes up) or correcting price/
    // description - the cover passed in is already the tracked EF entity
    // (fetched via GetIssuesForSeries), so this just persists the changes.
    public void UpdateCover(Cover cover)
    {
        _db.Covers.Update(cover);
        _db.SaveChanges();
    }

    public void ClearFailedChanges()
    {
        _db.ChangeTracker.Clear();
    }

    public Cover AddOrUpdateItem(
        string seriesName,
        int startYear,
        float issueNumber,
        IssueType type,
        string publisher,
        DateOnly? focDate,
        DateOnly? releaseDate,
        string coverLetter,
        string barcode,
        decimal price,
        int stockQuantity,
        string? description,
        string author = "",
        string penciler = "",
        string inker = "",
        string colorist = "",
        string letterer = "")
    {
        // Matched by the same normalized key AddEntriesAsIssues/
        // ImportService use (see SeriesNameKey) - a plain exact-name
        // match here would re-split "The Flash" from an already-existing
        // "Flash" row (or vice versa) the moment a manual add or UPC scan
        // typed/returned the name with a different "The " prefix than
        // whatever the weekly scrape already created, recreating the
        // exact duplicate-series bug AddEntriesAsIssues had.
        var seriesKey = SeriesNameKey.Normalize(seriesName);
        var series = _db.AllSeries.AsEnumerable().FirstOrDefault(s => SeriesNameKey.Normalize(s.Name) == seriesKey);
        if (series == null)
        {
            series = new Series { Name = seriesName, StartYear = startYear };
            _db.AllSeries.Add(series);
        }

        var issue = _db.Issues
            .Include(i => i.Covers)
            .FirstOrDefault(i => i.Series == series && i.IssueNumber == issueNumber && i.Type == type);

        if (issue == null)
        {
            issue = new Issue
            {
                Series = series,
                IssueNumber = issueNumber,
                Type = type,
                Publisher = publisher,
                FocDate = focDate,
                ReleaseDate = releaseDate,
                Author = author,
                Penciler = penciler,
                Inker = inker,
                Colorist = colorist,
                Letterer = letterer
            };
            _db.Issues.Add(issue);
        }
        else
        {
            issue.Publisher = publisher;
            issue.FocDate = focDate;
            issue.ReleaseDate = releaseDate;
            issue.Author = author;
            issue.Penciler = penciler;
            issue.Inker = inker;
            issue.Colorist = colorist;
            issue.Letterer = letterer;
        }

        var existingCover = _db.Covers.FirstOrDefault(c => c.Barcode == barcode);
        var belongsToThisIssue = issue.Id != 0 && existingCover != null && existingCover.IssueId == issue.Id;
        if (existingCover != null && !belongsToThisIssue)
        {
            throw new InvalidOperationException(
                $"Barcode {barcode} is already used by a different issue. Use a unique barcode for each cover.");
        }

        Cover cover;
        if (existingCover == null)
        {
            cover = new Cover
            {
                Barcode = barcode,
                CoverLetter = coverLetter,
                Price = price,
                StockQuantity = stockQuantity,
                Description = description
            };
            issue.Covers.Add(cover);
        }
        else
        {
            // Same barcode, same issue (or a brand new issue sharing Id 0
            // pre-save) - treat this as updating/receiving that item.
            existingCover.CoverLetter = coverLetter;
            existingCover.Price = price;
            existingCover.StockQuantity = stockQuantity;
            existingCover.Description = description;
            cover = existingCover;
        }

        _db.SaveChanges();
        return cover;
    }
}