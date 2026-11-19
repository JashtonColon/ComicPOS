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
}
