using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

// Pulls real FOC-cutoff-keyed solicitation data from comicade.com's public
// catalogue (comicade.com/catalogue/foc/{date}). comicade.com is a
// Laravel + Inertia.js app, and - unusually for a site with no official
// API - it exposes the actual database rows as a JSON payload alongside
// every page (an Inertia "page object"), not just rendered HTML. Setting
// the right request headers (see ComicadeFocService) gets that same JSON
// back directly, with no HTML parsing needed at all: exact FOC dates,
// exact release dates, real UPCs, real retail prices, cover image URLs,
// and clean separated series/issue-number fields, all exact rather than
// estimated or regex-parsed out of a title string.
//
// Comicade's own solicited horizon is currently shorter than
// comiclist.info's Extended Forecast (a couple of months out, not ~10),
// so this is a complement to IComicListService, not a replacement for it -
// but everything it does cover is exact, not estimated, which matters a
// lot for an FOC buy report specifically.
public interface IComicadeFocService
{
    // Walks forward week by week (every Wednesday, which is both
    // comicade.com's FOC-week key and the industry's standard street day)
    // starting from the current week, until the site's own solicited data
    // runs out (two consecutive empty weeks) or a safety cap is hit,
    // returning every issue found along the way. Throws
    // InvalidOperationException with a specific, readable reason if even
    // the very first week can't be fetched or parsed into something that
    // looks like real data (site layout/API changed, blocked request,
    // etc.) - same fail-loud contract as IComicListService. A later
    // week failing on its own is recorded and skipped rather than failing
    // the whole call, since partial real data beats none.
    List<ComicListEntry> GetUpcomingFocEntries();
}
