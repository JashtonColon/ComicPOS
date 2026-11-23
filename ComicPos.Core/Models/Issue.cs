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

    // Metron's own numeric id for this issue (see
    // ComicListEntry.MetronIssueId) - persisted here, unlike every other
    // Metron field, specifically so a LATER pass (AutoFetchService.
    // RetryMetronEnrichment) can re-fetch this exact issue's detail call
    // again for an issue that's already on file. Needed because the
    // first wave of Metron detail enrichment failures (2026-11-22's DRF
    // decimal-as-string bug) happened AFTER the issue itself was already
    // created - BuildCoversFromEntry only ever enriches a BRAND NEW
    // cover, so without this there would be no way to go back and
    // finish the job for an issue that's already been added. Null for
    // every non-Metron source (LOCG/ComicList/GCD/Comicade/Comic Vine).
    public int? MetronIssueId { get; set; }
}