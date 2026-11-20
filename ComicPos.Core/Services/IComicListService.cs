using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

// Pulls real shipping/solicitation data from comiclist.info - a
// long-running, publicly-crawlable (robots.txt allows everything) site
// that republishes the actual Diamond/Lunar-style comic lists, compiled
// across every publisher. This is real data the Comic Vine-based discovery
// in AutoFetchService can only approximate by guessing at publisher
// activity, and it covers two different things:
public interface IComicListService
{
    // Finds and parses this week's master list post - what's physically
    // shipping to stores THIS Wednesday. Throws InvalidOperationException
    // with a specific, readable reason if the post couldn't be found or
    // parsed into something that looks like a real list (changed site
    // layout, blocked request, dead link, etc.) - callers (AutoFetchService)
    // catch that and report it in the Check Now result rather than letting
    // the run fail, but the message is what makes a layout/parsing problem
    // diagnosable instead of a silent "added 0 issues."
    ComicListWeek GetThisWeeksMasterList();

    // Finds and parses every publisher's current "Extended Forecast" post -
    // solicited books shipping weeks to months out (comiclist.info's posts
    // run roughly 10 months ahead). This is the data that actually matters
    // for an FOC buy report: by the time a book shows up on the WEEKLY
    // master list above, FOC already passed 3 weeks ago. Each returned
    // entry carries its own ReleaseDate (unlike the weekly list, these
    // don't all ship the same week). Same fail-loud contract as above -
    // throws rather than returning an empty/null result on failure.
    List<ComicListEntry> GetExtendedForecastEntries();
}
