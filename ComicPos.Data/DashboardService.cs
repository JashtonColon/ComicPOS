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
        // Every issue AutoFetchService discovers (up to ~10 months ahead
        // via the Extended Forecast/Comicade FOC sources) starts at
        // StockQuantity 0 the moment it's added - long before it ever
        // ships. Without the ReleaseDate filter below, those hundreds of
        // not-yet-released covers all satisfy "<= threshold" and flood
        // this list, burying the actual problem (something that sold out)
        // under books nobody could possibly have restocked yet.
        var today = DateOnly.FromDateTime(DateTime.Today);

        return _db.Covers
            .Include(c => c.Issue).ThenInclude(i => i.Series)
            .Where(c => c.StockQuantity <= threshold && c.Issue.ReleaseDate != null && c.Issue.ReleaseDate <= today)
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

    public List<RecentSaleEntry> GetRecentSales(int count)
    {
        return _db.Sales
            .Include(s => s.Customer)
            .Include(s => s.Lines)
            .Where(s => !s.IsVoided)
            .OrderByDescending(s => s.SoldAt)
            .Take(count)
            .ToList()
            .Select(s => new RecentSaleEntry
            {
                SaleId = s.Id,
                SoldAt = s.SoldAt,
                CustomerName = s.Customer?.Name,
                ItemCount = s.Lines.Sum(l => l.Quantity),
                Total = s.Lines.Sum(l => l.PriceEach * l.Quantity * (1 - l.DiscountPercent / 100))
            })
            .ToList();
    }
}
