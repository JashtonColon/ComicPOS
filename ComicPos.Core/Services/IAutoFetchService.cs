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

    // The full step-by-step trace of this run (same text DebugLog writes
    // to checknow-debug.txt) - exposed here too so a caller like the web
    // app's New Books page can show it inline instead of making someone
    // go find that file on disk.
    public string Trace = "";
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

    // A separate, one-time/on-demand historical backfill - not something
    // that runs as part of the normal weekly Check Now flow, and never
    // run automatically (same spirit as RescanExistingBooks above).
    // Pulls every week of the given year from League of Comic Geeks (see
    // ILocgService) and feeds each week into the same series/issue/cover
    // creation logic as ComicList/GCD (AddEntriesAsIssues), with a
    // mandatory delay between weeks to respect LOCG's own robots.txt
    // crawl-delay - and, confirmed live, LOCG's own AJAX endpoint only
    // actually lets a request through roughly once every few minutes
    // regardless (see LocgService.FetchWeekJsonWithRetryAsync), so a
    // full year can take several hours in practice, not minutes. A
    // single real browser session is kept open for the whole run rather
    // than one per week - meant to be kicked off and left running in the
    // background, not something to repeat casually.
    //
    // onProgress, when given, is called after every single week (success
    // or failure) with a short one-line status - this is a long enough
    // run (hours, now that EnrichIssueDetails adds a detail-page fetch
    // per brand-new issue/cover too) that a caller with no feedback at
    // all until the very end looks hung even when it's working fine.
    // Called from a background thread (see NewBooks.razor's
    // Task.Run(() => AutoFetch.BackfillYearFromLocg(...))) - a caller on
    // Blazor Server's synchronization context needs to marshal back with
    // InvokeAsync(StateHasChanged) itself; this never assumes any
    // particular thread.
    AutoFetchResult BackfillYearFromLocg(int year, Action<string>? onProgress = null);

    // The PRIMARY discovery call as of 2026-11-22 (see IMetronService for
    // why Metron replaced LOCG in this role) - NewBooks.razor's "Check
    // Now" button calls this with a rolling window (recent past through
    // several weeks/months out, long enough to surface FOC deadlines
    // before they pass), and its "Backfill Year" button calls it with a
    // full calendar year to rebuild history after a catalog reset.
    // Internally chunked into weekly spans against Metron (even though
    // Metron's own pagination could do the whole range in one logical
    // call) purely so a failure partway through only loses that one
    // week's results, not the whole run, and so progress can be reported
    // the same granular way as BackfillYearFromLocg above. onProgress
    // fires once per week-chunk processed; same threading contract as
    // BackfillYearFromLocg (assume nothing about which thread calls this
    // on - marshal back to the UI thread yourself).
    AutoFetchResult CheckForNewIssuesViaMetron(DateOnly after, DateOnly before, Action<string>? onProgress = null);

    // One-time catch-up pass for Metron-sourced issues whose per-issue
    // detail call failed when they were first added (see
    // AutoFetchService.RetryMetronEnrichment's own doc comment - the
    // 2026-11-22 decimal-as-string bug is the concrete case this exists
    // for) - fills in real Price/UPC/Publisher/Description and adds any
    // cover variants Metron has on file that never got pulled in. Never
    // run automatically, same spirit as RescanExistingBooks above.
    AutoFetchResult RetryMetronEnrichment(Action<string>? onProgress = null);
}
