namespace ComicPos.Core.Services;

public class TopSellerEntry
{
    public string SeriesName { get; set; } = "";
    public int QuantitySold { get; set; }
}

public class LowStockEntry
{
    public string SeriesName { get; set; } = "";
    public int StartYear { get; set; }
    public float IssueNumber { get; set; }
    public string CoverLetter { get; set; } = "A";
    public int StockQuantity { get; set; }
}

public class RecentSaleEntry
{
    public int SaleId { get; set; }
    public DateTime SoldAt { get; set; }
    public string? CustomerName { get; set; }
    public int ItemCount { get; set; }
    public decimal Total { get; set; }
}

// Task #95 (2026-11-24): "an alternate cool-looking Dashboard view that
// doesn't show sales figures/stats" - these two back a pure cover-art
// wall instead (see Dashboard.razor's toggle between the original
// stat-row/panel-row view and this one). Deliberately no $ amounts
// anywhere on either entry.
public class CoverArtEntry
{
    public int CoverId { get; set; }
    public string SeriesName { get; set; } = "";
    public float IssueNumber { get; set; }
    public string CoverLetter { get; set; } = "A";
}

public class UpcomingReleaseEntry
{
    public int CoverId { get; set; }
    public string SeriesName { get; set; } = "";
    public float IssueNumber { get; set; }
    public DateOnly? ReleaseDate { get; set; }
}

public interface IDashboardService
{
    decimal GetRevenue(DateTime from, DateTime to);
    List<TopSellerEntry> GetTopSellingSeries(int days, int topN);
    List<LowStockEntry> GetLowStock(int threshold);
    List<RecentSaleEntry> GetRecentSales(int count);

    // Most-recently-added covers with real art on file - there's no
    // persisted "date added" on Cover (same gap noted elsewhere in this
    // project; would need a migration), so Id descending is used as a
    // reasonable "recently added" proxy, same kind of heuristic already
    // used in a few other places in this app without one.
    List<CoverArtEntry> GetRecentCoverArt(int count);

    // One tile per upcoming issue (Cover A only, so a multi-variant issue
    // doesn't crowd the wall with duplicates of itself), soonest first.
    List<UpcomingReleaseEntry> GetUpcomingReleases(int count);
}
