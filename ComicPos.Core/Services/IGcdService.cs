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
    // Comic Vine has already been asked and the issue is still missing a
    // cover (common for back-catalog Comic Vine's linked volume doesn't
    // cover, or genuinely has no art for). Looks the issue up directly by
    // series name + issue number (+ start year, to tell a relaunch #1
    // apart from an older series sharing the same title) via GCD's
    // "/series/name/.../issue/..." endpoint, picks the base (non-variant)
    // printing when more than one comes back, fetches that specific
    // issue for its real cover scan URL, downloads it, and saves it the
    // same way ComicVineService.DownloadCoverImage does. Returns null -
    // never throws - when GCD simply doesn't have this issue either or
    // has no cover scan on file for it; that is a normal outcome here,
    // not a failure, so AutoFetchService treats it exactly like "still
    // missing" rather than logging it as an error.
    string? FindAndDownloadCover(string seriesName, float issueNumber, int? startYear, string fileNameHint);
}
