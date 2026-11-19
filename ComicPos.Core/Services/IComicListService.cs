using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

// Pulls the current week's real shipping list from comiclist.info - a
// long-running, publicly-crawlable (robots.txt allows everything) site
// that republishes the actual Diamond/Lunar-style weekly comic list,
// compiled across every publisher. This is the real "what's shipping this
// Wednesday" list the Comic Vine-based discovery in AutoFetchService can
// only approximate by guessing at publisher activity.
public interface IComicListService
{
    // Finds and parses this week's master list post. Throws
    // InvalidOperationException with a specific, readable reason if the
    // post couldn't be found or parsed into something that looks like a
    // real list (changed site layout, blocked request, dead link, etc.) -
    // callers (AutoFetchService) catch that and report it in the Check Now
    // result rather than letting the run fail, but the message is what
    // makes a layout/parsing problem diagnosable instead of a silent
    // "added 0 issues."
    ComicListWeek GetThisWeeksMasterList();
}
