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
// LOCG is now the PRIMARY source for new issues - see NewBooks.razor's
// "Check Now (LOCG, <year>)" button, which calls
// AutoFetchService.BackfillYearFromLocg for the current year. Comic
// Vine/Metron/GCD (via Re-scan Existing) only run afterward as a
// fallback for whatever LOCG didn't have. One real browser session, a
// mandatory ~30s gap between weeks per LOCG's own robots.txt crawl-delay,
// and the weekly AJAX endpoint's own much harsher (roughly 1 request per
// ~4-5 minutes) quota on top of that - see FetchWeekJsonWithRetryAsync's
// own comment. AutoFetchService.BackfillYearFromLocg is the entry point
// that loops the weeks and calls this one week at a time.
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

    // Fills in entry.Upc and entry.FocDate from the issue's own LOCG
    // detail page (entry.DetailUrl) - the weekly list page doesn't carry
    // either (confirmed live: the real UPC and Final Order Cutoff date
    // only showed up on the per-issue page, in a "details-addtl-block"
    // div per field, each with a "name" label and a "value"). Best-effort
    // only: a missing DetailUrl, a layout this doesn't recognize, or any
    // fetch failure just leaves entry.Upc/FocDate as they were rather
    // than throwing - this is an enrichment, not something a whole
    // week's import should fail over. Paces itself with the same
    // robots.txt crawl-delay as GetWeekEntries; callers don't need their
    // own delay between calls. Only ever worth calling for a BRAND NEW
    // issue/cover (see AutoFetchService.BuildCoverFromEntry) - there's no
    // reason to re-fetch a detail page for something already on file.
    void EnrichIssueDetails(ComicListEntry entry);
}
