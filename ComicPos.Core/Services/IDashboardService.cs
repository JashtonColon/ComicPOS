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

public interface IDashboardService
{
    decimal GetRevenue(DateTime from, DateTime to);
    List<TopSellerEntry> GetTopSellingSeries(int days, int topN);
    List<LowStockEntry> GetLowStock(int threshold);
}
