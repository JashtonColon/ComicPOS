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
    public List<Issue> GetIssuesForSeries(string seriesName)
    {
        return _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .Where(i => i.Series.Name == seriesName)
            .ToList();
    }

    public List<Series> GetAllSeriesForDisplay()
    {
        return _db.AllSeries.ToList();
    }
}