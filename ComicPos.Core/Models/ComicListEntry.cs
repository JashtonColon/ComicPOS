namespace ComicPos.Core.Models;

// One line item from comiclist.info's weekly master shipping list (e.g.
// "Amazing Spider-Man #1003 (Cover A John Romita Jr.), $4.99"). RawTitle is
// kept as-is for display/debugging; SeriesName/IssueNumber/CoverLetter are
// best-effort parses out of it and can be null/blank when the title didn't
// match the usual "Name #123 (Cover X ...)" shape (trade paperbacks,
// graphic novels, non-comic merchandise, etc. - those are skipped by
// AutoFetchService rather than guessed at).
public class ComicListEntry
{
    public string Publisher { get; set; } = string.Empty;
    public string RawTitle { get; set; } = string.Empty;

    public string? SeriesName { get; set; }
    public float? IssueNumber { get; set; }
    public string CoverLetter { get; set; } = "A";

    public decimal? Price { get; set; }
    public bool PriceIsAskRetailer { get; set; }
}

// One week's worth of entries, plus the street date they all ship on
// (parsed from the post's title/URL, e.g. "...-november-18-2026").
public class ComicListWeek
{
    public DateOnly ReleaseDate { get; set; }
    public List<ComicListEntry> Entries { get; set; } = new();
}
