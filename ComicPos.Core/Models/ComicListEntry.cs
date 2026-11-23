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

    // The publisher's own solicit blurb - what shows up in the New Books
    // detail popup as the issue's description. comicade.com is the only
    // source that has this text at all.
    public string? Description { get; set; }

    // The issue's own LOCG detail page (e.g.
    // https://leagueofcomicgeeks.com/comic/1096934/absolute-flash-21) -
    // only ever set by LocgService, since that's the only source whose
    // weekly list links out to a separate per-issue page at all. Not
    // persisted anywhere - purely a hand-off so
    // AutoFetchService.BuildCoverFromEntry knows where to send
    // ILocgService.EnrichIssueDetails for a brand-new issue/cover, since
    // the weekly list itself doesn't carry UPC or a real FOC date.
    public string? DetailUrl { get; set; }

    // Everything below is only ever set by MetronService - see
    // IMetronService.GetIssuesInDateRange. Metron's own API returns the
    // real volume identity and start year directly alongside every issue
    // it lists (no name/year guessing needed at all), which is precise
    // enough to use as the PRIMARY lookup/creation key for a Series row
    // in AutoFetchService.AddEntriesAsIssues, ahead of the name-based
    // normalized-key matching every other source still needs.

    // Metron's own numeric id for this specific issue - handed to
    // IMetronService.EnrichIssueDetails to fetch price/UPC/publisher for
    // a brand-new issue, the same hand-off DetailUrl above does for LOCG.
    public int? MetronIssueId { get; set; }

    // Metron's own numeric id for the VOLUME this issue belongs to -
    // stored onto Series.MetronSeriesId the first time a series resolves
    // through this, so every later run can match by this exact id
    // instead of by name at all.
    public int? MetronSeriesId { get; set; }

    // The real year this volume began, straight from Metron's own
    // series.year_began - used as a brand-new Series row's StartYear
    // instead of AddEntriesAsIssues' old "just use today's year" guess,
    // which stamped a wrong placeholder onto every series discovered
    // from a weekly scrape (a 1990s-launched series discovered via this
    // week's new issue used to get StartYear = this week's year).
    public int? SeriesStartYearHint { get; set; }

    // True when CoverLetter above is a best-effort fallback (LOCG
    // couldn't parse a real "Cover X" out of a variant's name, so it
    // used a single placeholder letter) rather than something actually
    // read off the source. AddEntriesAsIssues/AddMissingCoverVariants
    // treat a guessed letter as "needs ITS OWN free slot" instead of
    // "this is definitely Cover B" - three unlettered LOCG variants for
    // the same issue used to all land on the literal same letter ("B")
    // and silently overwrite each other's spot in the grid (confirmed
    // live: Ultimates #19 showed three different variants all labeled
    // Cover B). A confidently-parsed letter (this stays false) is still
    // treated as a real identity, so a later re-scrape of the SAME
    // cover is recognized and not duplicated.
    public bool CoverLetterIsGuess { get; set; }

    // Every OTHER cover Metron has on file for this exact issue - only
    // ever populated by MetronService.EnrichIssueDetails, from that same
    // per-issue detail call's own "variants" list (name/upc/price/image
    // per variant). Metron's cheap list call (GetIssuesInDateRange) only
    // ever returns the primary/Cover-A edition, so a brand-new issue
    // starts with zero of these and gets them filled in right before
    // AutoFetchService turns this entry into Cover rows - one extra Cover
    // row per entry here, each with its own real image/UPC/price instead
    // of just Cover A ever showing up.
    public List<MetronVariantInfo> Variants { get; set; } = new();

    // The raw free-text label a variant was announced under (LOCG's own
    // variant text - "Cover B Ryan Sook", "Ryan Sook Variant", etc.) -
    // only ever set by LocgService, since Metron's equivalent per-variant
    // text already lives on MetronVariantInfo.Name above (one ComicListEntry
    // per LOCG variant vs. one entry total with several Variants for
    // Metron - see AutoFetchService.BuildCoversFromEntry, which reads
    // whichever of the two applies). Used both to show something more
    // useful than a bare guessed letter (Cover.CoverArtist) and, for a
    // variant CoverLetterParser couldn't find a real letter in, as the
    // only stable way to recognize "this is the same variant a later
    // re-scrape already added" instead of a bare UPC (LOCG variants
    // essentially never have one).
    public string? VariantLabel { get; set; }
}

// One Metron-listed variant cover for an issue - see ComicListEntry.
// Variants above. Deliberately a flat DTO, not an EF entity: it only
// ever exists transiently, on the way to becoming a real Cover row.
public class MetronVariantInfo
{
    public string? Name { get; set; }
    public string? Upc { get; set; }
    public decimal? Price { get; set; }
    public string? ImageUrl { get; set; }
}

// One week's worth of entries, plus the street date they all ship on
// (parsed from the post's title/URL, e.g. "...-november-18-2026").
public class ComicListWeek
{
    public DateOnly ReleaseDate { get; set; }
    public List<ComicListEntry> Entries { get; set; } = new();
}
