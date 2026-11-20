namespace ComicPos.Core.Services;

public class FocReportSubscriber
{
    public string CustomerName { get; set; } = "";
    public string PreferredCover { get; set; } = "A";
    public int Quantity { get; set; } = 1;

    // True if PreferredCover is actually one of the covers this specific
    // printing shipped with. False means the customer's standing
    // preference points at a cover that doesn't exist for this issue (a
    // stale pick from a prior arc's cover lineup, a typo that was never
    // caught, etc.) - worth a human look before ordering on autopilot.
    public bool CoverIsAvailable { get; set; } = true;
}

public class FocReportEntry
{
    public string SeriesName { get; set; } = "";
    public int StartYear { get; set; }
    public float IssueNumber { get; set; }
    public DateOnly? FocDate { get; set; }
    public List<FocReportSubscriber> Subscribers { get; set; } = new();

    // The actual cover letters this issue has on file (from Comic
    // Vine/ComicList/GCD/manual entry), so a buyer can tell "nobody's
    // pulling Cover C" from "there IS no Cover C for this one."
    public List<string> AvailableCoverLetters { get; set; } = new();

    // Total copies to buy, not just subscriber count - one customer
    // wanting 2 copies of the same cover is 2 units on the order, not 1.
    public int TotalQuantity => Subscribers.Sum(s => s.Quantity);
}

public interface IFocReportService
{
    // Only returns issues that at least one active subscriber wants - this
    // is a buy list for fulfilling pull lists, not a general release calendar.
    List<FocReportEntry> GetUpcomingFocReport(int daysAhead);
}
