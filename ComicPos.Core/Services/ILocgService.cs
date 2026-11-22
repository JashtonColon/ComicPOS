using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

// League of Comic Geeks (leagueofcomicgeeks.com) - a new-releases source
// much richer than comiclist.info for this purpose (direct cover images,
// clean per-issue variant grouping, exact release dates) but, unlike
// ComicListService/GcdService, sitting behind Cloudflare bot-protection.
// That rules out a plain HTTP scrape: the implementation has to drive a
// real, visible Chrome window (Playwright, persistent profile) to pass
// Cloudflare's challenge once, then call LOCG's own `get_comics` AJAX
// endpoint - the same one the site's own date-picker/filters use - via an
// in-page fetch() so every later week reuses that same already-cleared
// session instead of reloading the whole page (and risking a fresh
// challenge) every time.
//
// This is NOT part of the normal weekly Check Now discovery chain (see
// AutoFetchService.RunCheckForNewIssues/DiscoverFromComicList and
// friends) - it's a separate, on-demand, much heavier operation (one real
// browser session, a mandatory ~30s gap between weeks per LOCG's own
// robots.txt crawl-delay) meant to backfill a whole year of history on
// request, not run on a schedule. AutoFetchService.BackfillYearFromLocg is
// the entry point that loops the weeks and calls this one week at a time.
public interface ILocgService
{
    // Everything LOCG lists as releasing in the single week that starts
    // on this Monday (LOCG's weekly pages are always Monday-to-Monday -
    // confirmed by driving the site's own date picker and watching which
    // URL it navigated to). Throws InvalidOperationException with a
    // specific, readable reason on failure (Cloudflare challenge never
    // cleared, site unreachable, response shape changed) - same fail-loud
    // contract as IComicListService/IGcdService, so a caller looping many
    // weeks can report exactly which one failed and why instead of
    // silently skipping it.
    List<ComicListEntry> GetWeekEntries(DateOnly mondayOfWeek);
}
