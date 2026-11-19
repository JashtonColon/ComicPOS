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
        string? description)
    {
        var series = _db.AllSeries.FirstOrDefault(s => s.Name == seriesName);
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
                ReleaseDate = releaseDate
            };
            _db.Issues.Add(issue);
        }
        else
        {
            issue.Publisher = publisher;
            issue.FocDate = focDate;
            issue.ReleaseDate = releaseDate;
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