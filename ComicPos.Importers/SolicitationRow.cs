namespace ComicPos.Importers;

// One row of a parsed solicitation CSV - a single cover of a single issue.
public class SolicitationRow
{
    public string SeriesName { get; set; } = string.Empty;
    public int StartYear { get; set; }
    public float IssueNumber { get; set; }
    public string Publisher { get; set; } = string.Empty;
    public string CoverLetter { get; set; } = "A";
    public string Barcode { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateOnly? FocDate { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public string Type { get; set; } = "SingleIssue";

    // How many copies physically arrived in this shipment. 0 (the default)
    // means "solicited but not received yet" - the normal case for a new
    // solicitation file. A positive number bumps stock when importing a
    // packing list / receiving sheet for comics already in the system.
    public int ReceivedQty { get; set; } = 0;
}
