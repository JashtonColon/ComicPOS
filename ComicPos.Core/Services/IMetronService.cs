using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

// Metron (metron.cloud) - a second, independent community comic database,
// separate from both Comic Vine and GCD, with its own account and its own
// rate limit (confirmed on its own documentation: 20 requests/minute, 5,000
// requests/day for an authenticated account - there is no anonymous tier,
// unlike GCD, so this always needs a Settings-configured account to do
// anything at all). As of 2026-11-22 this is also the PRIMARY source for
// discovering new issues at all (NewBooks.razor's "Check Now" button -
// see GetIssuesInDateRange), replacing the LOCG scraper that held that
// role briefly before it: Metron is a plain token-authenticated REST API
// with no Cloudflare or other bot-protection to click through, its own
// series data carries a real year_began directly (used as
// Series.MetronSeriesId/StartYear, eliminating the name+year guessing
// PickBestVolumeMatch otherwise needs), and its documented rate limit is
// far gentler than LOCG's empirically-discovered ~4-5 minute AJAX quota
// window. Also still used as the PRIMARY cover-art fallback for
// RescanExistingBooks (GCD only runs after this one misses or is rate-
// limited - see AutoFetchService) - richer per-issue data than GCD
// (exact cover/in-store/FOC dates, full creator credits) and a far more
// reliable uptime history this session (GCD's own Cloudflare protection
// has repeatedly 403'd this app for 24+ hours at a stretch). A single
// call to its /issue/ list endpoint, filtered by series_name + number,
// already returns a cover image URL directly in the list result - no
// separate detail call needed, unlike GCD's two-step list-then-detail
// dance.
public interface IMetronService
{
    // Lists every issue Metron has on file with a store/street date
    // between after and before (inclusive), used as the primary source
    // for "what's new/upcoming" discovery instead of LOCG's weekly-page
    // scrape. One cheap list call per page (Metron's own pagination,
    // followed via its "next" link) rather than one call per week like
    // the old LOCG backfill needed - a whole year is one logical
    // request here, just paginated. onProgress fires once per page
    // fetched, the same live-progress hookup NewBooks.razor already
    // wires up for the LOCG button, so a long date range still shows
    // it's moving instead of looking stuck.
    //
    // Deliberately does NOT fetch Publisher/Price/Upc for every result -
    // those only exist on Metron's per-issue DETAIL endpoint, and a
    // detail call for every one of a date range's results (hundreds,
    // industry-wide, for any real span) would turn one cheap list call
    // into exactly the kind of per-issue cost this method exists to
    // avoid. Those three fields come back blank here; EnrichIssueDetails
    // below fills them in later, but only for an issue AddEntriesAsIssues
    // has confirmed is genuinely brand new - see AutoFetchService.
    // BuildCoverFromEntry.
    List<ComicListEntry> GetIssuesInDateRange(DateOnly after, DateOnly before, Action<string>? onProgress = null);

    // Fills in Publisher/Price/Upc (and double-checks FocDate/
    // ReleaseDate/CoverImageUrl, though GetIssuesInDateRange's list call
    // already has those) for one issue, via Metron's own numeric
    // MetronIssueId - no name/number search involved, unlike
    // FindAndDownloadCover, so there's no ambiguity to resolve at all.
    // Mirrors ILocgService.EnrichIssueDetails' exact contract: mutates
    // entry in place, best-effort in spirit (meant to be called only for
    // a brand-new issue/cover, never repeated for one already on file),
    // but THROWS on failure rather than swallowing it, so a caller's
    // try/catch (see AutoFetchService.BuildCoverFromEntry) can actually
    // log what went wrong instead of silently leaving fields blank.
    void EnrichIssueDetails(ComicListEntry entry);
    // Never throws - see GcdCoverLookupResult's sibling, MetronCoverLookupResult,
    // for the same "always say why" contract.
    //
    // expectedDate is this exact issue's own ReleaseDate/FocDate (not the
    // series' often-wrong StartYear scalar - confirmed live: Wonder
    // Woman's series.StartYear was 1942, and even the issue's own release
    // YEAR alone (2026) wasn't enough, because Metron's series_year_began
    // means the VOLUME's start year, not this issue's release year - a
    // modern ongoing series that started a few years before 2026 doesn't
    // match "year began 2026" at all). Used to pick whichever candidate
    // result's own cover_date sits closest to expectedDate, client-side,
    // rather than as a request-side filter Metron would just reject.
    //
    // maxCoversNeeded tells this call how many distinct cover images the
    // caller can actually use (how many of this issue's Cover rows - A,
    // B, C... - are still missing art). Left at the default of 1, this
    // behaves exactly as before: one list-endpoint call, one image. Asked
    // for more than 1, it also fetches the matched issue's own detail
    // endpoint (confirmed via mokkari's published schema: Issue.variants
    // is a list[Variant], each with its own name + image, separate from
    // the primary Issue.image) and downloads the primary cover plus every
    // variant, up to maxCoversNeeded total, returned via AllCovers below.
    // Found live this session: every Cover letter (A, B, C, D...) for one
    // issue was silently getting the SAME single downloaded image, even
    // though Metron's own site shows genuinely distinct art per variant -
    // this is the fix, at the cost of one extra paced API call per issue
    // that actually needs it.
    MetronCoverLookupResult FindAndDownloadCover(string seriesName, float issueNumber, DateOnly? expectedDate, string fileNameHint, int maxCoversNeeded = 1);

    // Looks an issue up directly by its UPC/barcode instead of by series
    // name + number - a UPC is (in principle) unique to one specific
    // printing, so this is a far more precise match than the name-based
    // search above when it works at all. Used when a worker scans a
    // barcode at intake that isn't in ComicPos yet, to auto-fill (and
    // then auto-save) a brand new item instead of requiring everything
    // typed in by hand.
    //
    // IMPORTANT, UNCONFIRMED: "upc=" as a filter on Metron's /issue/ list
    // endpoint is a reasonable guess (upc is a real, documented field on
    // Metron's Issue model, and this API already supports series_name=/
    // number= as plain Django-style field filters) but I have not been
    // able to test it live - metron.cloud isn't reachable from my own
    // sandbox. If a real scan of a book you know Metron has comes back
    // "no match," that's the first thing to suspect, not that the book
    // isn't in Metron at all - tell me what you scanned.
    //
    // A single comic printing can carry a DIFFERENT upc per cover variant
    // (Variant.upc, separate from the primary Issue.upc) - the barcode
    // physically on the book in hand is just as likely to be a variant's
    // as the main Cover A's, so this checks both and returns whichever
    // one actually matched, with a best-effort guess at that variant's
    // cover letter parsed from its name (e.g. "Cover B" -> "B").
    MetronUpcLookupResult FindIssueByUpc(string upc);

    // Same series_name+number search FindAndDownloadCover uses, but
    // returns just Metron's own numeric issue id instead of downloading
    // anything - for an issue that's already on file in this app but
    // predates Issue.MetronIssueId existing at all (added before
    // 2026-11-23), so RetryMetronEnrichment has no id to call
    // EnrichIssueDetails with yet and has to re-find it by name/number
    // first, one time, the same way the issue was originally matched
    // during discovery. Null when nothing matches - same "no match"
    // outcome as FindAndDownloadCover's own Fail(), just without a
    // Reason/Detail string since this is only ever used internally.
    int? FindIssueId(string seriesName, float issueNumber, DateOnly? expectedDate);
}

// One downloaded cover image, as part of a multi-cover result (see
// MetronCoverLookupResult.AllCovers / GcdCoverLookupResult.AllCovers).
// Label is Metron/GCD's own name for the variant ("Cover B", "2nd
// Printing Variant", etc.) or null for the primary/base cover - matched
// against ComicPos's own Cover.CoverLetter values by AutoFetchService,
// on a best-effort basis, since there's no guarantee a source's variant
// naming lines up cleanly with a single letter.
public class CoverCandidate
{
    public string? Label { get; set; }
    public string ImagePath { get; set; } = "";
}

public class MetronCoverLookupResult
{
    public string? ImagePath { get; set; }

    // A short, FIXED category string (no interpolated specifics) so many
    // results can be grouped and counted together in a run's trace log.
    public string Reason { get; set; } = "";

    // Optional, per-call specific text (an exception message, a URL, a
    // snippet of an unexpected response) - useful as a handful of
    // concrete examples alongside the grouped counts.
    public string? Detail { get; set; }

    // Set when Metron itself returned 429, or when something (its own
    // bot-protection, seen returning an HTML challenge page instead of
    // JSON on metron.cloud's own website during testing) blocked the
    // request outright. Either way, further calls this run would almost
    // certainly fail the same way, so the caller should stop calling
    // Metron at all for the rest of the run instead of retrying it issue
    // by issue.
    public bool IsRateLimited { get; set; }

    public bool Success => !string.IsNullOrWhiteSpace(ImagePath);

    // Every cover image this call actually downloaded - just [primary]
    // when maxCoversNeeded was 1 (or variants genuinely don't exist for
    // this issue), primary + variants when it was asked for more than
    // one and found them. Always populated alongside ImagePath (never
    // just one or the other) so a caller can treat this as the single
    // source of truth once it exists.
    public List<CoverCandidate> AllCovers { get; set; } = new();
}

// Result of FindIssueByUpc - a successful match effectively discovers a
// whole new Issue+Cover from scratch (unlike FindAndDownloadCover, which
// only ever fills in a cover image for an issue ComicPos already knows
// about), so this carries everything AddItemModal needs to create one:
// series identity, the specific issue, and whatever of publisher/price/
// dates/credits Metron actually had on file.
public class MetronUpcLookupResult
{
    public string Reason { get; set; } = "";
    public string? Detail { get; set; }
    public bool IsRateLimited { get; set; }

    public string? SeriesName { get; set; }
    public int? SeriesStartYear { get; set; }
    public float IssueNumber { get; set; }
    public string? Publisher { get; set; }
    public decimal? Price { get; set; }

    // Metron's own three dates, confirmed distinct via mokkari's schema -
    // store_date is the real on-sale/street date (ComicPos's ReleaseDate),
    // foc_date lines up with ComicPos's FocDate directly, and cover_date
    // (the nominal cover month) isn't used here at all.
    public DateOnly? StoreDate { get; set; }
    public DateOnly? FocDate { get; set; }

    // Best-effort guess at which Cover row this is, parsed from whichever
    // variant's own upc matched the scan (or "A" when it matched the
    // primary issue upc, or no variant info was available at all).
    public string CoverLetterGuess { get; set; } = "A";

    public string? DownloadedImagePath { get; set; }

    public string Author { get; set; } = "";
    public string Penciler { get; set; } = "";
    public string Inker { get; set; } = "";
    public string Colorist { get; set; } = "";
    public string Letterer { get; set; } = "";

    public bool Success => !string.IsNullOrWhiteSpace(SeriesName);
}
