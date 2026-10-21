namespace ComicPos.Core.Models;

public class Issue{

    public Series Series { get; set; } = null!;
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public float IssueNumber { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Penciler { get; set; } = string.Empty;
    public string Inker { get; set; } = string.Empty;
    public string Colorist { get; set; } = string.Empty;
    public string Letterer { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty; // impprting data needs to me trim to upper
    public DateOnly? FocDate { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public List<Cover> Covers { get; set; } = new();
    public IssueType Type { get; set; } = IssueType.SingleIssue;
}