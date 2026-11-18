namespace ComicPos.Core.Services;

public class FocReportSubscriber
{
    public string CustomerName { get; set; } = "";
    public string PreferredCover { get; set; } = "A";
}

public class FocReportEntry
{
    public string SeriesName { get; set; } = "";
    public int StartYear { get; set; }
    public float IssueNumber { get; set; }
    public DateOnly? FocDate { get; set; }
    public List<FocReportSubscriber> Subscribers { get; set; } = new();
}

public interface IFocReportService
{
    // Only returns issues that at least one active subscriber wants - this
    // is a buy list for fulfilling pull lists, not a general release calendar.
    List<FocReportEntry> GetUpcomingFocReport(int daysAhead);
}
