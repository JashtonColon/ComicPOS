using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

// Grand Comics Database (comics.org) - a real, official (no API key
// needed) REST API, unlike the HTML scraping ComicListService does. Its
// on_sale_weekly endpoint returns issues GCD has catalogued as on sale in
// a given ISO week.
//
// Weaker than ComicListService for this job, though: no exact ship date
// (GCD only gives a month-level publication_date), and no publisher name
// on the issue itself - each one costs two extra lookups (issue -> series
// -> publisher) to resolve. Kept as a third, lowest-priority discovery
// source behind ComicList and Comic Vine, not a replacement for either.
public interface IGcdService
{
    // Throws InvalidOperationException with a specific, readable reason if
    // the API is unreachable, its response shape doesn't match what's
    // documented, or nothing parseable came back - same contract as
    // IComicListService.GetThisWeeksMasterList: AutoFetchService catches
    // this and reports it in the Check Now result instead of letting the
    // run fail, so a real problem is diagnosable instead of silently
    // showing up as "added 0."
    ComicListWeek GetThisWeeksOnSaleIssues();

    // Fallback cover-art source for RescanExistingBooks - tried only after
    // Metron and Comic Vine have already been asked and the issue is
    // still missing a cover. Looks the issue up directly by series name +
    // issue number via GCD's "/series/name/.../issue/..." endpoint (no
    // year filter in the request itself - see expectedDate below for why)
    // and, among whatever comes back, picks whichever result's own
    // publication_date sits closest to expectedDate (falling back to the
    // base/non-variant printing when dates can't be compared at all),
    // fetches that specific issue for its real cover scan URL, downloads
    // it, and saves it the same way ComicVineService.DownloadCoverImage
    // does. Never throws - GCD simply not having this issue, or having
    // no cover scan on file for it, is a normal outcome here, not a
    // failure. Unlike the old bool-returning version, the result always
    // says WHY it didn't find anything (a real "GCD doesn't have it" vs.
    // a request that failed outright) - that distinction is the only way
    // a "0 covers backfilled from GCD" run can be told apart from "GCD
    // just doesn't carry any of these" instead of staying a total
    // mystery.
    //
    // expectedDate is the one real-world date this exact physical issue
    // is tied to (its own ReleaseDate/FocDate, not the series' often-
    // wrong StartYear scalar - confirmed live: a character relaunched
    // across decades, like Wonder Woman, can have the SAME issue number
    // published multiple times, and only the actual date tells those
    // apart). It's used to pick the closest-matching result client-side
    // rather than as a server-side filter - GCD's own "/year/" path
    // segment turned out to mean the SERIES' start year, not this
    // issue's own date, so asking for "year 2026" on a series that
    // actually started years earlier just returned nothing at all, every
    // time, even for a issue that genuinely releases in 2026.
    // maxCoversNeeded: see IMetronService's own copy of this parameter for
    // the full rationale (same bug, same fix shape). For GCD specifically:
    // the list endpoint used here already returns separate rows for
    // variant printings of the same issue number (the pre-existing
    // variant_of field) - when more than one cover is actually needed,
    // this takes the N closest-dated distinct results instead of just
    // the single best one, fetches each one's own detail (GCD's list view
    // has no cover field at all - see the class comment below), and
    // returns every image it found via AllCovers.
    GcdCoverLookupResult FindAndDownloadCover(string seriesName, float issueNumber, DateOnly? expectedDate, string fileNameHint, int maxCoversNeeded = 1);
}

public class GcdCoverLookupResult
{
    public string? ImagePath { get; set; }

    // A short, FIXED category string (no interpolated specifics) so many
    // results can be grouped and counted - e.g. "no match in GCD",
    // "GCD request failed: TaskCanceledException". Always set, success or
    // failure.
    public string Reason { get; set; } = "";

    // Optional, per-call specific text (an exception message, the actual
    // URL tried) - too varied to group by, but useful as a handful of
    // concrete examples alongside the grouped counts above.
    public string? Detail { get; set; }

    // Set when GCD itself returned 429 Too Many Requests - a hard stop,
    // not a miss. Anonymous GCD access is capped at roughly 30
    // requests/hour (confirmed on GCD's own mailing list), so once this
    // trips, every further call this run will fail the exact same way;
    // the caller should stop calling GCD at all for the rest of the run
    // instead of burning through the whole remaining catalog re-hitting
    // an already-tripped limit one issue at a time.
    public bool IsRateLimited { get; set; }

    public bool Success => !string.IsNullOrWhiteSpace(ImagePath);

    // See MetronCoverLookupResult.AllCovers (CoverCandidate is shared
    // between both services, defined in IMetronService.cs) - always
    // populated alongside ImagePath, just [primary] in the common
    // maxCoversNeeded=1 case.
    public List<CoverCandidate> AllCovers { get; set; } = new();
}
