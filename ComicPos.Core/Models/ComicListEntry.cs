namespace ComicPos.Core.Models;

// One line item from comiclist.info - either a weekly master shipping list
// entry (e.g. "Amazing Spider-Man #1003 (Cover A John Romita Jr.), $4.99")
// or an Extended Forecast entry (a per-publisher solicitation that ships
// weeks or months out). RawTitle is kept as-is for display/debugging;
// SeriesName/IssueNumber/CoverLetter are best-effort parses out of it and
// can be null/blank when the title didn't match the usual "Name #123
// (Cover X ...)" shape (trade paperbacks, graphic novels, non-comic
// merchandise, etc. - those are skipped by AutoFetchService rather than
// guessed at).
public class ComicListEntry
{
    public string Publisher { get; set; } = string.Empty;
    public string RawTitle { get; set; } = string.Empty;

    public string? SeriesName { get; set; }
    public float? IssueNumber { get; set; }
    public string CoverLetter { get; set; } = "A";

    public decimal? Price { get; set; }
    public bool PriceIsAskRetailer { get; set; }

    // Only set for Extended Forecast entries, which each carry their own
    // ship date (they span months, unlike a weekly master list where every
    // entry ships the same week). Null means "use the containing
    // ComicListWeek's single ReleaseDate instead" - see AutoFetchService.
    public DateOnly? ReleaseDate { get; set; }

    // Everything below is only ever set by ComicadeFocService - comiclist.info
    // and comics.org (GCD) have no equivalent data, so these stay null for
    // entries from those sources and AddEntriesAsIssues falls back to its
    // existing estimates/placeholders exactly as before.

    // A real FOC cutoff date, not the "release date minus 3 weeks" guess
    // used everywhere else.
    public DateOnly? FocDate { get; set; }

    // A real UPC/barcode from the distributor feed, usable as the actual
    // inventory barcode instead of a generated placeholder.
    public string? Upc { get; set; }

    // A direct, downloadable cover image URL - when set, AddEntriesAsIssues
    // downloads it immediately so the issue has a real cover right away,
    // instead of needing AutoFetchService's later Comic Vine backfill pass.
    public string? CoverImageUrl { get; set; }
}

// One week's worth of entries, plus the street date they all ship on
// (parsed from the post's title/URL, e.g. "...-november-18-2026").
public class ComicListWeek
{
    public DateOnly ReleaseDate { get; set; }
    public List<ComicListEntry> Entries { get; set; } = new();
}
