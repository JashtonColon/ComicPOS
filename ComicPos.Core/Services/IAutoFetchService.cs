using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public class AutoFetchResult
{
    public int SeriesChecked;
    public int SeriesAutoLinked;
    public int NewIssuesAdded;
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
}
