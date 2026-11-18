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
}
