using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public class AutoFetchResult
{
    public int SeriesChecked;
    public int SeriesAutoLinked;
    public int NewIssuesAdded;

    // Only ever incremented by RescanExistingBooks - a backfilled cover on
    // an issue that already existed isn't a "new issue", so it's kept
    // separate from NewIssuesAdded instead of conflating the two in the
    // result popup.
    public int CoversBackfilled;

    public List<string> Added { get; } = new();
    public List<string> Errors { get; } = new();
}

// Fully automatic: every series in inventory gets matched to a Comic Vine
// volume by name/start-year on its own (no manual confirmation step), and
// checked for issues released in roughly the last 3 months through the
// next 3 months. Meant to run on its own about once a week, not be driven
// by hand from a link/unlink screen.
public interface IAutoFetchService
{
    // True if it's been 7+ days since the last check (or none has ever run).
    bool IsWeeklyCheckDue();

    DateTime? LastCheckedAt();

    // Auto-links any not-yet-linked series (skipping ones with no confident
    // name match rather than guessing), then pulls new issues for every
    // linked series. Updates the last-checked timestamp when done.
    AutoFetchResult CheckForNewIssues();

    // Everything in the last-3-months/next-3-months window, for the New
    // Books tab - whatever's already in the database (Comic Vine finds,
    // CSV imports, or hand-entered items alike), not a fresh API call.
    List<Issue> GetNewBooks();

    // A manual, explicit "fix my existing data" action - separate from the
    // normal weekly/Check Now flow and never run automatically. Re-verifies
    // every already-linked series' Comic Vine volume (undoing a bad link
    // made before better matching logic existed, e.g. a decades-old
    // character linked to its original volume instead of whichever one is
    // actually publishing the issue numbers already on file), and
    // backfills missing cover art for every issue on file - not just
    // whatever falls inside the normal rolling few-months discovery
    // window, and ignoring the normal recheck cooldown. This can mean a
    // lot of Comic Vine calls for a store with a large catalog, so it's
    // meant to be run occasionally by hand, not on a schedule.
    AutoFetchResult RescanExistingBooks();
}
