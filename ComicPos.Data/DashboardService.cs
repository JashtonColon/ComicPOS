using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class DashboardService : IDashboardService
{
    private readonly ComicDbContext _db;

    public DashboardService(ComicDbContext db)
    {
        _db = db;
    }

    public decimal GetRevenue(DateTime from, DateTime to)
    {
        var lines = _db.SaleLines
            .Include(l => l.Sale)
            .Where(l => l.Sale.SoldAt >= from && l.Sale.SoldAt <= to && !l.Sale.IsVoided)
            .ToList();

        return lines.Sum(l => l.PriceEach * l.Quantity * (1 - l.DiscountPercent / 100));
    }

    public List<TopSellerEntry> GetTopSellingSeries(int days, int topN)
    {
        var since = DateTime.Now.AddDays(-days);

        return _db.SaleLines
            .Include(l => l.Sale)
            .Include(l => l.Cover).ThenInclude(c => c.Issue).ThenInclude(i => i.Series)
            .Where(l => l.Sale.SoldAt >= since && !l.Sale.IsVoided)
            .ToList()
            .GroupBy(l => l.Cover.Issue.Series.Name)
            .Select(g => new TopSellerEntry { SeriesName = g.Key, QuantitySold = g.Sum(l => l.Quantity) })
            .OrderByDescending(e => e.QuantitySold)
            .Take(topN)
            .ToList();
    }

    public List<LowStockEntry> GetLowStock(int threshold)
    {
        return _db.Covers
            .Include(c => c.Issue).ThenInclude(i => i.Series)
            .Where(c => c.StockQuantity <= threshold)
            .ToList()
            .Select(c => new LowStockEntry
            {
                SeriesName = c.Issue.Series.Name,
                StartYear = c.Issue.Series.StartYear,
                IssueNumber = c.Issue.IssueNumber,
                CoverLetter = c.CoverLetter,
                StockQuantity = c.StockQuantity
            })
            .OrderBy(e => e.StockQuantity)
            .ThenBy(e => e.SeriesName)
            .ToList();
    }
}
