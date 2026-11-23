using System.Text.RegularExpressions;
using System.Threading;
using ComicPos.Core;
using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class AutoFetchService : IAutoFetchService
{
    private readonly ComicDbContext _db;
    private readonly IComicVineService _comicVine;
    private readonly IComicListService _comicList;
    private readonly IComicadeFocService _comicadeFoc;
    private readonly IGcdService _gcd;
    private readonly IMetronService _metron;
    private readonly ILocgService _locg;
    private readonly IStoreSettingsService _storeSettings;

    public AutoFetchService(ComicDbContext db, IComicVineService comicVine, IComicListService comicList, IComicadeFocService comicadeFoc, IGcdService gcd, IMetronService metron, ILocgService locg, IStoreSettingsService storeSettings)
    {
        _db = db;
        _comicVine = comicVine;
        _comicList = comicList;
        _comicadeFoc = comicadeFoc;
        _gcd = gcd;
        _metron = metron;
        _locg = locg;
        _storeSettings = storeSettings;
    }

    public DateTime? LastCheckedAt() => _storeSettings.Load().LastAutoFetchRun;

    public bool IsWeeklyCheckDue()
    {
        var last = LastCheckedAt();
        return last == null || (DateTime.Now - last.Value).TotalDays >= 7;
    }

    public AutoFetchResult CheckForNewIssues()
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Check Now run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        try
        {
            RunCheckForNewIssues(result, trace);
            return result;
        }
        finally
        {
            // Written whether the run succeeds, partially fails, or
            // throws - this is meant to answer "what actually happened"
            // without needing another test round just to find out.
            trace.AppendLine();
            trace.AppendLine(new string('=', 60));
            trace.AppendLine($"Final: checked {result.SeriesChecked} series, linked {result.SeriesAutoLinked}, added {result.NewIssuesAdded} new issue(s), backfilled {result.CoversBackfilled} cover(s).");
            if (result.Added.Count > 0)
            {
                trace.AppendLine();
                trace.AppendLine("Added:");
                foreach (var line in result.Added) trace.AppendLine("  " + line);
            }
            if (result.Errors.Count > 0)
            {
                trace.AppendLine();
                trace.AppendLine("Errors:");
                foreach (var line in result.Errors) trace.AppendLine("  " + line);
            }
            var traceText = trace.ToString();
            result.Trace = traceText;
            DebugLog.WriteCheckNowLog(traceText);
        }
    }

    // How long a successfully-checked series is left alone before it's
    // worth hitting Comic Vine for it again. Street dates for an ongoing
    // series don't change minute to minute, so re-checking every single
    // series on every single Check Now click - manual testing included -
    // used to burn through Comic Vine's free-tier hourly quota for zero
    // new information, which is exactly what was producing the repeated
    // "rate limit hit too many times in a row" errors. A series that
    // errored (couldn't be checked at all) has no LastCheckedForNewIssues
    // update, so it's never skipped by this and keeps retrying every run.
    private static readonly TimeSpan RecheckCooldown = TimeSpan.FromHours(3);

    // A separate, longer cooldown specifically for FAILED auto-link
    // attempts. ComicList/Extended Forecast create a Series for every
    // trade paperback, hardcover, graphic novel, and one-shot they find
    // (see ComicListService) - none of those will ever match a Comic Vine
    // "volume" (Comic Vine only tracks ongoing single-issue series), so
    // without this cooldown, every single one of them would get a fresh,
    // doomed-to-fail Comic Vine search on every single Check Now run,
    // forever - exactly the kind of wasted-quota pattern RecheckCooldown
    // above was built to stop, just on the "never going to link" side
    // instead of the "already linked" side. 24h still gives a genuinely
    // new ongoing series a same-day-ish chance to link once Comic Vine's
    // catalog catches up, without hammering it for TP/GN noise meanwhile.
    private static readonly TimeSpan AutoLinkRetryCooldown = TimeSpan.FromHours(24);

    private void RunCheckForNewIssues(AutoFetchResult result, System.Text.StringBuilder trace)
    {
        // ThenInclude(Covers) matters now, not just for display - CheckOneSeries'
        // cover-image backfill (BackfillCoverImage, below) reads issue.Covers,
        // and without this EF Core would hand back an empty collection for
        // every issue (no lazy-loading proxies are configured), silently
        // making the backfill a no-op.
        var allSeries = _db.AllSeries.Include(s => s.Issues).ThenInclude(i => i.Covers).ToList();
        trace.AppendLine($"Rechecking {allSeries.Count} series already in inventory...");

        // A snapshot of what the store already carried BEFORE this run's
        // discovery steps touch anything - the per-series auto-link/recheck
        // loop further down iterates THIS, not the live allSeries list, so
        // a series a discovery step creates moments from now doesn't also
        // immediately eat a second round of Comic Vine calls in the very
        // same run. It gets its own first look on the next run instead,
        // same as always. allSeries itself keeps growing as discovery
        // runs - DiscoverFromComicList/ExtendedForecast/ComicadeFoc/
        // NewNumberOnes still need the full, growing list to dedupe
        // against each other and against what's already in inventory.
        var existingSeries = allSeries.ToList();

        // allSeries only covers books the store already carries. A series
        // that has never been carried has no row here at all, so it would
        // never be found by the per-series loop below - this is what
        // misses brand-new #1 launches and future solicitations. Four
        // sources fill that gap, run in order, and run BEFORE the
        // per-series loop below:
        //
        //  1. DiscoverFromComicList - the real WEEKLY shipping list from
        //     comiclist.info (see ComicListService.GetThisWeeksMasterList).
        //     What's physically shipping THIS Wednesday: real titles,
        //     real prices, real variant covers. By the time something
        //     shows up here, its FOC already passed 3 weeks ago - this
        //     step is about catching current inventory, not FOC buying.
        //     Any Series it creates gets added into allSeries here so the
        //     later steps see it as already-known and don't duplicate it.
        //  2. DiscoverFromExtendedForecast - comiclist.info's per-publisher
        //     forward SOLICITATION data, ~10 months out (see
        //     ComicListService.GetExtendedForecastEntries). This is the
        //     one that actually matters for FOC buying decisions, since
        //     it's the only source that knows about a book before its FOC
        //     date arrives.
        //  3. DiscoverFromComicadeFoc - comicade.com's FOC-cutoff catalogue
        //     (see ComicadeFocService). Shorter horizon than Extended
        //     Forecast (a couple of months, not ~10) but exact rather than
        //     estimated: a real FOC date, real UPC, real price and a real
        //     cover image per issue, not a guess. Entries from this source
        //     already carry a real cover image, so they never need the
        //     Comic Vine cover-backfill pass (BackfillCoverImage) the way
        //     ComicList/Extended Forecast entries do.
        //  4. DiscoverNewNumberOnes - Comic Vine-based discovery (either
        //     targeted by Tracked Publishers or a global #1 scan). Runs
        //     regardless, both to catch anything the other sources missed
        //     and as the sole source if they're ever all unreachable
        //     (every source here fails soft per-call - an error is caught
        //     and reported, not thrown - so one breaking never breaks the
        //     others).
        //
        // This whole block is now deliberately placed BEFORE the
        // per-series loop, not after it. Comic Vine's free-tier rate
        // limit is one shared, finite budget for the WHOLE run, and a
        // store with hundreds of already-carried series can easily burn
        // through all of it just rechecking/backfilling books it already
        // has - which used to leave nothing left for this step, the one
        // that actually finds brand-new launches and solicitations from
        // the publishers the store told Settings > Tracked Publishers it
        // cares about. Missing a new launch is a bigger loss for a
        // buy-before-FOC report than a slightly stale recheck of a book
        // already in the system, so discovery goes first and gets first
        // claim on the budget.
        //
        // GCD (comics.org) discovery used to be a source here too but is
        // now disabled: ComicList's scraper covers the whole week on its
        // own, which made GCD purely redundant and its only contribution
        // was noisy 429 rate-limit errors. DiscoverFromGcd/GcdService are
        // left in the codebase, unused, in case ComicList ever needs a
        // second source again.
        var beforeComicList = result.NewIssuesAdded;
        DiscoverFromComicList(allSeries, result);
        trace.AppendLine($"ComicList: +{result.NewIssuesAdded - beforeComicList} issue(s).");

        var beforeForecast = result.NewIssuesAdded;
        DiscoverFromExtendedForecast(allSeries, result);
        trace.AppendLine($"Extended Forecast: +{result.NewIssuesAdded - beforeForecast} issue(s).");

        var beforeComicadeFoc = result.NewIssuesAdded;
        DiscoverFromComicadeFoc(allSeries, result);
        trace.AppendLine($"Comicade FOC: +{result.NewIssuesAdded - beforeComicadeFoc} issue(s).");

        var beforeComicVine = result.NewIssuesAdded;
        DiscoverNewNumberOnes(allSeries, result);
        trace.AppendLine($"Comic Vine discovery: +{result.NewIssuesAdded - beforeComicVine} issue(s).");

        var skippedCooldown = 0;
        var skippedAutoLinkCooldown = 0;
        foreach (var series in existingSeries)
        {
            try
            {
                if (series.ComicVineVolumeId == null)
                {
                    if (series.LastAutoLinkAttempt != null &&
                        DateTime.Now - series.LastAutoLinkAttempt.Value < AutoLinkRetryCooldown)
                    {
                        skippedAutoLinkCooldown++;
                        continue; // tried to link this one recently and it didn't match - don't retry yet
                    }
                    if (!TryAutoLink(series, result)) continue; // no confident match - leave unlinked, try again after the cooldown
                }
                else
                {
                    if (series.LastCheckedForNewIssues != null &&
                        DateTime.Now - series.LastCheckedForNewIssues.Value < RecheckCooldown)
                    {
                        skippedCooldown++;
                        continue; // checked too recently to be worth another Comic Vine call
                    }

                    // Re-examine an already-linked series' volume match
                    // every time it's otherwise due for a normal recheck,
                    // not just during RescanExistingBooks (which only
                    // calls ReverifyVolumeLink for series that still have
                    // a missing cover - see its own loop). Without this, a
                    // series whose covers are already all filled in (like
                    // Wonder Woman's, once Re-scan finished backfilling
                    // them) never got its link reconsidered again, ever -
                    // confirmed live: Wonder Woman stayed linked to the
                    // wrong 1942 Comic Vine volume indefinitely because
                    // nothing still called this for it once its covers
                    // were complete.
                    ReverifyVolumeLink(series, result);
                }

                CheckOneSeries(series, result);
            }
            catch (Exception ex) when (IsRateLimitError(ex))
            {
                // Comic Vine was already saturated by the discovery steps
                // above (or by an earlier series this same loop) - every
                // remaining series would just fail the exact same way, one
                // at a time, for no new information. Stopping here instead
                // of grinding through the rest leaves them completely
                // untouched (no cooldown stamped), so they get a full,
                // fair attempt on the next Check Now instead of one that
                // was doomed before it started.
                result.Errors.Add("Comic Vine's rate limit is still active - stopped rechecking the rest of this run's already-known series early; they'll get a full look on the next Check Now (or the next scheduled weekly check).");
                break;
            }
        }
        if (skippedAutoLinkCooldown > 0)
            trace.AppendLine($"  ({skippedAutoLinkCooldown} unlinked series skipped - auto-link attempted within the last {AutoLinkRetryCooldown.TotalHours:0}h and didn't match, e.g. TPs/GNs that can never match a Comic Vine volume.)");
        trace.AppendLine($"  done - linked {result.SeriesAutoLinked}, {result.NewIssuesAdded} issue(s) so far from existing series" +
            (skippedCooldown > 0 ? $" ({skippedCooldown} skipped - checked within the last {RecheckCooldown.TotalHours:0}h)." : "."));


        var saved = true;
        try
        {
            _db.SaveChanges();
            trace.AppendLine("SaveChanges: OK.");
        }
        catch (Exception ex)
        {
            saved = false;
            result.Errors.Add($"Some changes could not be saved: {ex.Message}");
            trace.AppendLine($"SaveChanges: FAILED - {ex.Message}");

            // A failed SaveChanges does NOT roll back the change tracker -
            // every Series/Issue/Cover added this run is still sitting in
            // memory marked "Added". Left alone, the next unrelated
            // SaveChanges() anywhere in the app (even a totally separate
            // screen) would silently flush these half-built rows too,
            // producing duplicates. Clear them out instead.
            _db.ChangeTracker.Clear();
        }

        // Only mark the week's check "done" if it actually saved - if it
        // failed, leave LastAutoFetchRun alone so the next check (manual or
        // weekly) retries instead of silently skipping for another week.
        if (saved)
        {
            var settings = _storeSettings.Load();
            settings.LastAutoFetchRun = DateTime.Now;
            _storeSettings.Save(settings);
        }
    }

    // A rate-limit failure (see ComicVineService) never got a real answer
    // from Comic Vine either way - not "this series doesn't match", just
    // "couldn't ask right now". Checked by every Comic Vine call site in
    // this file that needs to tell the two apart: a genuine no-match is
    // worth remembering (so a TP/GN that will never link isn't retried
    // every single run), a rate limit is not (penalizing a series with a
    // whole cooldown for a question that was never actually asked just
    // delays finding out the real answer).
    private static bool IsRateLimitError(Exception ex) =>
        ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    // Picks the best Comic Vine match for a series with no human in the
    // loop: an exact (case-insensitive) name match is required. If more
    // than one volume shares that exact name, the one whose start year
    // matches the series wins when the series' StartYear is trustworthy;
    // otherwise (see the comment below) the volume whose issue count best
    // fits what's already on file wins. A series with no confident name
    // match is left unlinked and just skipped this run instead of
    // guessing - it's retried on the next check.
    private bool TryAutoLink(Series series, AutoFetchResult result)
    {
        List<ComicVineVolumeMatch> matches;
        try
        {
            matches = _comicVine.SearchVolumes(series.Name);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{series.Name}: {ex.Message}");
            // A rate-limited attempt isn't a genuine "no match" - see
            // IsRateLimitError - so LastAutoLinkAttempt is deliberately
            // left unstamped here and the exception is rethrown so the
            // circuit breaker in RunCheckForNewIssues's per-series loop
            // can stop early instead of grinding through every remaining
            // series the exact same losing way.
            if (IsRateLimitError(ex)) throw;
            series.LastAutoLinkAttempt = DateTime.Now;
            return false;
        }

        // A genuine attempt that got a real answer from Comic Vine (a
        // match or a confident non-match) - stamped here, once we know
        // this wasn't just a rate limit.
        series.LastAutoLinkAttempt = DateTime.Now;

        var best = PickBestVolumeMatch(matches, series.Name, series.StartYear, series.Issues);

        // A huge share of the store's "never linked, still showing a
        // blank cover" series turned out to be TPs/HCs/Omnibuses - their
        // scraped name carries a format suffix ("Giant Days Volume 1 TP",
        // "...Omnibus Volume 1 HC (Cover A...)") that Comic Vine never
        // puts in a real volume's name, so the exact-name search above
        // almost always comes back with nothing at all to match against -
        // not because Comic Vine doesn't know the book, but because it
        // was never asked about it by a name Comic Vine would recognize.
        if (best == null)
            best = TryLinkAsCollectedEdition(series, result);

        if (best == null) return false;

        series.ComicVineVolumeId = best.Id;
        SyncStartYear(series, best);
        result.SeriesAutoLinked++;
        return true;
    }

    // Comic Vine catalogs the underlying property under its own plain
    // name, never with a scraped collected-edition's format baked in -
    // see TryAutoLink. Strips a known format marker (Omnibus, Volume N,
    // TP, HC, GN, a "(Cover A ...)" printing) and retries the search
    // against whatever's left, keeping the same trailing "(YYYY)"
    // disambiguator every other series here already relies on (see
    // PickBestVolumeMatch) so this doesn't throw away the one piece of
    // information meant to tell same-named relaunches apart.
    //
    // Gated purely on whether the NAME carries one of those format
    // markers (StripCollectedEditionSuffix returning non-null), not on
    // Issue.Type - every scraped source (AddEntriesAsIssues/
    // AddIssueIfNew) stamps every issue IssueType.SingleIssue regardless
    // of real format, so a TP/HC/Omnibus from any automated source would
    // never actually carry IssueType.CollectedEdition to check against. A
    // series whose name doesn't look like a collected edition at all
    // falls through untouched - a failed match on an ordinary single-
    // issue/mixed series' exact scraped name means something else is
    // wrong (a real typo, a genuinely obscure/unlisted series), not this,
    // and guessing at a stripped name there would risk exactly the kind
    // of wrong-volume mislink that caused the "Superman (1939)" bug
    // earlier - this only ever widens the search for a case that was
    // otherwise guaranteed to never link at all.
    private ComicVineVolumeMatch? TryLinkAsCollectedEdition(Series series, AutoFetchResult result)
    {
        if (series.Issues.Count == 0) return null;

        var stripped = StripCollectedEditionSuffix(series.Name);
        if (stripped == null) return null;

        List<ComicVineVolumeMatch> matches;
        try
        {
            matches = _comicVine.SearchVolumes(stripped);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{series.Name}: {ex.Message}");
            if (IsRateLimitError(ex)) throw;
            return null;
        }

        return PickBestVolumeMatch(matches, stripped, series.StartYear, series.Issues);
    }

    private static readonly Regex CollectedEditionMarker = new(
        @"\b(Omnibus|Volume\s+\d+|Vol\.?\s*\d+|TP|HC|GN)\b|\(Cover\s+[A-Za-z]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingYear = new(@"\((\d{4})\)\s*$", RegexOptions.Compiled);

    // Returns a cleaned-up title to retry the search with, or null if
    // this name doesn't look like a collected edition at all (nothing to
    // strip, so there's no point retrying with an identical query).
    private static string? StripCollectedEditionSuffix(string seriesName)
    {
        var marker = CollectedEditionMarker.Match(seriesName);
        if (!marker.Success) return null;

        var yearMatch = TrailingYear.Match(seriesName);
        var baseTitle = seriesName.Substring(0, marker.Index).Trim().TrimEnd(':', '-', ',').Trim();
        if (string.IsNullOrWhiteSpace(baseTitle)) return null;

        var stripped = yearMatch.Success ? $"{baseTitle} ({yearMatch.Groups[1].Value})" : baseTitle;
        return string.Equals(stripped, seriesName, StringComparison.OrdinalIgnoreCase) ? null : stripped;
    }

    // Shared by TryAutoLink (a not-yet-linked series) and ReverifyVolumeLink
    // (re-checking an already-linked one during RescanExistingBooks) so the
    // matching rule lives in exactly one place: an exact (case-insensitive)
    // name match is required; among those, the one whose start year
    // matches the series wins when the series' StartYear is trustworthy.
    // How many issues behind a solicited-but-not-yet-published issue
    // number a volume's own Comic Vine issue count is still allowed to be
    // and count as "covers this issue". Extended Forecast solicitations
    // run up to ~10 months ahead (see ComicListService), so by the time a
    // brand-new Series row exists for one, the volume that will actually
    // publish it often hasn't been catalogued that far yet - confirmed
    // live: a fresh "Batman" row created for a solicited (not yet
    // published) Batman #17 matched Comic Vine's original 1940 legacy-
    // numbered volume (hundreds of issues, trivially ">= 17") instead of
    // the real current volume, because the real current volume's own
    // issue count hadn't reached 17 yet and so failed a strict ">="
    // check - leaving the ancient volume as the ONLY "qualifying"
    // candidate in the set, and therefore "smallest qualifying" by
    // default even though it was the one genuinely wrong choice. This
    // constant lets a volume within striking distance BELOW the target
    // still qualify; "smallest qualifying count wins" still naturally
    // favors it over a legacy volume's much larger count once both do.
    private const int SolicitationLeadBufferIssues = 12;

    private static bool VolumeCoversIssue(ComicVineVolumeMatch match, float maxKnownIssue)
        => match.IssueCount >= maxKnownIssue - SolicitationLeadBufferIssues;

    private static ComicVineVolumeMatch? PickBestVolumeMatch(List<ComicVineVolumeMatch> matches, string seriesName, int seriesStartYear, List<Issue> knownIssues)
    {
        var exact = matches.Where(m => string.Equals(m.Name, seriesName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0) return null;

        // Issue-count evidence is checked FIRST, before StartYear, not as
        // a fallback for when StartYear fails to match - and this order
        // matters. A StartYear coincidence used to be trusted outright,
        // but a decades-spanning legacy volume's issue count is always
        // huge, so it ALWAYS numerically "covers" any modest known issue
        // number - a plain issue-count sanity check on the StartYear
        // match can never catch it that way (tried that; confirmed live,
        // it changed nothing - Batman/Superman/Wonder Woman all landed
        // right back on their old legacy volumes). The only thing that
        // reliably beats the legacy volume is comparing it AGAINST every
        // other qualifying candidate and taking the SMALLEST qualifying
        // issue count - the current/ongoing volume for a character with
        // a long publishing history almost always has far fewer issues
        // on file than the original run, even once both "qualify". That
        // comparison has to happen before StartYear ever gets consulted,
        // or a StartYear match (especially one stamped by an earlier
        // WRONG link - see SyncStartYear - which makes it coincide with
        // that exact wrong volume's own real StartYear forever after, a
        // self-reinforcing trap) short-circuits it every time, exactly
        // as it did for Wonder Woman #42 even after the first fix above.
        if (knownIssues.Count > 0)
        {
            var maxKnownIssue = knownIssues.Max(i => i.IssueNumber);
            var qualifying = exact.Where(m => VolumeCoversIssue(m, maxKnownIssue)).ToList();
            if (qualifying.Count > 0)
                return qualifying.OrderBy(m => m.IssueCount).First();
        }

        // No issue-count evidence at all (nothing qualifies yet, or this
        // series has no issues on file to judge by) - StartYear is the
        // next-best signal, then "most issues so far" as a last resort
        // guess when even that comes up empty.
        return exact.FirstOrDefault(m => m.StartYear == seriesStartYear)
            ?? exact.OrderByDescending(m => m.IssueCount).First();
    }

    // Comic Vine's own StartYear for a matched volume is the one
    // trustworthy source this app has for what a series' displayed year
    // should be - everywhere a Series row gets CREATED (AddEntriesAsIssues,
    // ImportService, InventoryService.AddOrUpdateItem), StartYear is just
    // a placeholder (today's date, or whatever a CSV row happened to
    // carry), and nothing ever revisited it afterward even once a real
    // Comic Vine link was confirmed - confirmed live: "Wonder Woman" kept
    // displaying "(1942)" next to a 2026 solicitation because
    // PickBestVolumeMatch's own fix (the smallest-qualifying-count rule
    // above) only ever corrected ComicVineVolumeId, never StartYear
    // itself. Called from both TryAutoLink and ReverifyVolumeLink so a
    // series' displayed year self-corrects the moment its Comic Vine link
    // is confirmed or re-confirmed, instead of staying wrong forever once
    // a bad placeholder gets stamped at creation.
    private static void SyncStartYear(Series series, ComicVineVolumeMatch match)
    {
        if (match.StartYear is int y && y > 0 && series.StartYear != y)
            series.StartYear = y;
    }

    // Current + last 3 months + next 3 months of street dates - a rolling
    // 6-month window, re-checked every run (already-known issue numbers are
    // always skipped, so running this repeatedly is safe).
    private void CheckOneSeries(Series series, AutoFetchResult result)
    {
        result.SeriesChecked++;

        var (since, until) = DiscoveryWindow();

        List<ComicVineNewIssue> cvIssues;
        try
        {
            cvIssues = _comicVine.GetIssuesInRange(series.ComicVineVolumeId!.Value, since, until);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{series.Name}: {ex.Message}");
            // Let the circuit breaker in the caller's loop stop the whole
            // run early instead of grinding through every remaining
            // series one at a time against a rate limit that isn't going
            // away mid-run - see IsRateLimitError.
            if (IsRateLimitError(ex)) throw;
            return;
        }

        var knownNumbers = series.Issues.Select(i => i.IssueNumber).ToHashSet();

        foreach (var cvIssue in cvIssues)
        {
            if (knownNumbers.Contains(cvIssue.IssueNumber))
                BackfillCoverImage(series, cvIssue, result);
            else
                AddIssueIfNew(series, cvIssue, knownNumbers, result);
        }

        series.LastCheckedForNewIssues = DateTime.Now;
    }

    // ComicList/Extended Forecast-sourced issues (AddEntriesAsIssues) are
    // created with no cover image at all - those scraped pages have no
    // image URLs to give one (the weekly list is plain text, and Extended
    // Forecast titles only link to an Amazon affiliate page). That's most
    // issues in the store, since Comic Vine discovery is only a small
    // slice of what gets added. Once a series is linked to Comic Vine and
    // this same issue number turns up here, that's the first real chance
    // to fill the gap - so instead of only adding brand-new numbers, an
    // already-known one with a still-blank cover gets backfilled from the
    // same Comic Vine image data AddIssueIfNew already uses for new ones.
    // Comic Vine gives one image per issue, not one per cover letter, so
    // every cover on the issue (A, B, C...) ends up sharing that same
    // downloaded file - a real cover beats a blank one for every variant.
    private void BackfillCoverImage(Series series, ComicVineNewIssue cvIssue, AutoFetchResult result)
    {
        if (string.IsNullOrWhiteSpace(cvIssue.ImageUrl)) return;

        var issue = series.Issues.FirstOrDefault(i => i.IssueNumber == cvIssue.IssueNumber);
        if (issue == null) return;

        var missingCovers = issue.Covers.Where(c => string.IsNullOrWhiteSpace(c.CoverImagePath)).ToList();
        if (missingCovers.Count == 0) return;

        string? imagePath;
        try
        {
            var hint = $"{series.Name}_{cvIssue.IssueNumber:0.##}_{Guid.NewGuid():N}".Replace(" ", "_");
            imagePath = _comicVine.DownloadCoverImage(cvIssue.ImageUrl, hint);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{series.Name} #{cvIssue.IssueNumber:0.##}: cover image download failed: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(imagePath)) return;

        // Comic Vine's own per-issue API gives exactly ONE image
        // (cvIssue.ImageUrl) - unlike Metron/GCD's CoverCandidate list,
        // there's no label saying whether it's Cover A or a B/C/D
        // variant. The old code stamped it onto every still-missing
        // Cover row for the issue, duplicating one image across all of
        // them - confirmed live: Absolute Flash #21's Covers B, C and D
        // all ended up with the IDENTICAL file once Metron had already
        // filled A in an earlier run. The honest read is that this one
        // image is the issue's main (Cover A) art: it goes there if A
        // still needs it, or to the sole missing cover when there's only
        // one row total - never duplicated across multiple still-blank
        // variant letters, which are left blank for Metron/GCD (which DO
        // return genuinely distinct art per variant) to fill properly.
        var mainCover = missingCovers.FirstOrDefault(c => string.Equals(c.CoverLetter?.Trim(), "A", StringComparison.OrdinalIgnoreCase))
            ?? (missingCovers.Count == 1 ? missingCovers[0] : null);
        if (mainCover == null) return; // multiple non-A variants still missing - nothing sensible to assign a single unlabeled image to

        mainCover.CoverImagePath = imagePath;

        // Previously silent - Check Now's own trace never said which
        // exact issue got touched here, only a run-wide "added N new
        // issue(s)" total that said nothing about covers. Logged the
        // same way AddIssueIfNew logs a brand-new issue, so the trace
        // can answer "what exactly changed" issue by issue, not just
        // "something happened."
        result.CoversBackfilled++;
        result.Added.Add($"{series.Name} #{cvIssue.IssueNumber:0.##}: cover backfilled from Comic Vine (Cover {mainCover.CoverLetter} updated).");
    }

    private static (DateOnly since, DateOnly until) DiscoveryWindow() =>
        (DateOnly.FromDateTime(DateTime.Today.AddMonths(-3)), DateOnly.FromDateTime(DateTime.Today.AddMonths(3)));

    // A manual, explicit "fix my existing data" action (see
    // IAutoFetchService.RescanExistingBooks) - never run automatically,
    // and deliberately ignores the normal recheck/auto-link cooldowns so
    // it actually revisits everything instead of skipping most of the
    // catalog the way a normal Check Now would.
    public AutoFetchResult RescanExistingBooks()
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Re-scan existing books run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        // Local-only, no Comic Vine calls - safe to always run first,
        // rate limit or not, and it changes which issues actually count
        // as "missing a cover" below (a placeholder graphic masqueraded
        // as a real one until just now).
        var placeholdersCleared = ClearPlaceholderCovers();
        if (placeholdersCleared > 0)
            trace.AppendLine($"Found {placeholdersCleared} cover(s) that were actually a source's generic \"not ready yet\" placeholder graphic, not real art - cleared so they're re-fetched below instead of staying stuck.");

        // Every series with at least one cover still missing art - not
        // just the ones already linked to a Comic Vine volume (the old
        // filter here). Metron (tried below for every series, see the
        // priority note further down) looks an issue up directly by
        // series name + issue number, so it needs no Comic Vine link at
        // all - a series that could never auto-link (a TP/GN, or one
        // Comic Vine simply doesn't carry) still gets a Metron attempt.
        var allSeries = _db.AllSeries
            .Include(s => s.Issues).ThenInclude(i => i.Covers)
            .Where(s => s.Issues.Any(i => i.Covers.Any(c => string.IsNullOrWhiteSpace(c.CoverImagePath))))
            .ToList();

        // Comic-Vine-linked series first, while its (shared, depletable)
        // budget is freshest - Metron/GCD below run for every series
        // regardless of this ordering, since they have their own
        // separate budgets.
        allSeries = allSeries
            .OrderByDescending(s => s.ComicVineVolumeId != null)
            .ToList();
        trace.AppendLine($"Re-checking {allSeries.Count} series with at least one missing cover (Comic Vine where linked, then Metron, then GCD as a last resort)...");

        // Once Comic Vine's rate limit trips, every further call to it
        // this run will just fail the same way - so instead of the old
        // behavior (one rate-limited series aborting the ENTIRE rest of
        // the run, GCD included - the actual cause of "many covers still
        // missing" even after a run that reported backfilling some),
        // Comic Vine is simply skipped for whatever series are left while
        // GCD, a separate and independent source, keeps going for all of
        // them regardless.
        var comicVineRateLimited = false;
        // GCD's anonymous access is capped around 30 requests/hour
        // (confirmed on GCD's own gcd-tech mailing list) - a hard quota,
        // not something a longer per-call delay can avoid. Once it trips,
        // every further call this run fails the exact same way, so this
        // stops calling GCD AT ALL for the rest of the run instead of
        // burning through the whole remaining catalog re-hitting an
        // already-tripped limit one issue at a time (confirmed: a run
        // against 504 series made 813 GCD calls that all failed with 429
        // after the first ~30 succeeded or failed normally).
        var gcdRateLimited = false;
        var gcdAttempts = 0;
        var backfilledFromGcd = 0;
        var metronRateLimited = false;
        var metronAttempts = 0;
        var backfilledFromMetron = 0;
        // Tallies WHY a GCD or Metron attempt didn't produce a cover,
        // grouped by a fixed reason string (see GcdCoverLookupResult /
        // MetronCoverLookupResult) - the only way to tell "neither
        // genuinely carries any of these" apart from "every call is
        // silently failing" instead of both looking like an identical
        // "backfilled 0" result. Both sources share these two
        // collections - each Reason string already names its own source
        // ("GCD ..." / "Metron ...") so a mixed breakdown stays readable.
        var gcdReasonCounts = new Dictionary<string, int>();
        var gcdSampleDetails = new List<string>();

        foreach (var series in allSeries)
        {
            result.SeriesChecked++;
            var missingBefore = series.Issues.Count(i => i.Covers.Any(c => string.IsNullOrWhiteSpace(c.CoverImagePath)));

            if (series.ComicVineVolumeId != null && !comicVineRateLimited)
            {
                try
                {
                    ReverifyVolumeLink(series, result);
                    BackfillFromComicVine(series, result);
                }
                catch (Exception ex) when (IsRateLimitError(ex))
                {
                    comicVineRateLimited = true;
                    result.Errors.Add("Comic Vine's rate limit is active - skipping it for the rest of this run (GCD lookups still run for every remaining series). Run Re-scan again later to give Comic Vine another pass.");
                }
            }

            if (!gcdRateLimited || !metronRateLimited)
            {
                foreach (var issue in series.Issues.Where(i => i.Covers.Any(c => string.IsNullOrWhiteSpace(c.CoverImagePath))).ToList())
                {
                    var backfilled = false;

                    // Metron goes first now, GCD second as a last resort -
                    // not just GCD's rate limit (20/min, same as Metron's),
                    // but its anonymous-access cap (~30/hour, a hard wall
                    // for a whole run) and its Cloudflare bot-protection
                    // (403s that can take 24+ hours to clear, confirmed
                    // live this session) make it the far less reliable of
                    // the two. Metron's own per-issue data is also simply
                    // richer and less ambiguous - its series names carry
                    // their own year disambiguator ("Absolute Flash
                    // (2025)"), and it gives exact cover/in-store/FOC
                    // dates plus full creator credits, none of which GCD's
                    // response shape here is used for yet. GCD still runs
                    // whenever Metron misses or is rate-limited - a second
                    // independent source is still worth trying, just no
                    // longer the first one asked.
                    if (!metronRateLimited)
                    {
                        metronAttempts++;
                        var metronResult = BackfillCoverFromMetron(series, issue);
                        if (metronResult.Success)
                        {
                            backfilledFromMetron++;
                            backfilled = true;
                        }
                        else if (metronResult.IsRateLimited)
                        {
                            metronRateLimited = true;
                            result.Errors.Add($"Metron stopped responding normally after {metronAttempts} attempt(s) - skipping it for the rest of this run. Reason: {metronResult.Reason}.");
                        }
                        else
                        {
                            gcdReasonCounts[metronResult.Reason] = gcdReasonCounts.GetValueOrDefault(metronResult.Reason) + 1;
                            if (gcdSampleDetails.Count < 10 && !string.IsNullOrWhiteSpace(metronResult.Detail))
                                gcdSampleDetails.Add($"{series.Name} #{issue.IssueNumber:0.##}: {metronResult.Reason} - {metronResult.Detail}");
                        }
                    }

                    // GCD is only tried when Metron didn't come through for
                    // this exact issue (missed, or rate-limited) - a
                    // fallback now, not a co-equal first attempt.
                    if (!backfilled && !gcdRateLimited)
                    {
                        gcdAttempts++;
                        var gcdResult = BackfillCoverFromGcd(series, issue);
                        if (gcdResult.Success)
                        {
                            backfilledFromGcd++;
                        }
                        else if (gcdResult.IsRateLimited)
                        {
                            gcdRateLimited = true;
                            result.Errors.Add($"GCD stopped responding normally after {gcdAttempts} attempt(s) - skipping it for the rest of this run. Reason: {gcdResult.Reason}.");
                        }
                        else
                        {
                            gcdReasonCounts[gcdResult.Reason] = gcdReasonCounts.GetValueOrDefault(gcdResult.Reason) + 1;
                            if (gcdSampleDetails.Count < 10 && !string.IsNullOrWhiteSpace(gcdResult.Detail))
                                gcdSampleDetails.Add($"{series.Name} #{issue.IssueNumber:0.##}: {gcdResult.Reason} - {gcdResult.Detail}");
                        }
                    }

                    if (gcdRateLimited && metronRateLimited) break; // nothing left that could still succeed for this series
                }
            }

            var missingAfter = series.Issues.Count(i => i.Covers.Any(c => string.IsNullOrWhiteSpace(c.CoverImagePath)));
            var backfilledThisSeries = Math.Max(0, missingBefore - missingAfter);
            result.CoversBackfilled += backfilledThisSeries;

            // Logged per-series (not just the one run-wide total) so a
            // future run's log can actually answer "did THIS series get
            // fixed?" instead of only "something, somewhere, got fixed."
            if (backfilledThisSeries > 0)
                result.Added.Add($"{series.Name}: backfilled {backfilledThisSeries} cover(s) ({missingAfter} still missing).");
        }

        trace.AppendLine($"Metron backfilled {backfilledFromMetron} cover(s) out of {metronAttempts} attempt(s); GCD (fallback) backfilled {backfilledFromGcd} cover(s) out of {gcdAttempts} attempt(s) (on top of whatever Comic Vine backfilled above).");
        if (gcdReasonCounts.Count > 0)
        {
            trace.AppendLine("GCD/Metron misses, by reason:");
            foreach (var kv in gcdReasonCounts.OrderByDescending(kv => kv.Value))
                trace.AppendLine($"  {kv.Value}x: {kv.Key}");
            if (gcdSampleDetails.Count > 0)
            {
                trace.AppendLine("Sample GCD/Metron misses (with detail):");
                foreach (var line in gcdSampleDetails) trace.AppendLine("  " + line);
            }
        }

        try
        {
            _db.SaveChanges();
            trace.AppendLine("SaveChanges: OK.");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Some changes could not be saved: {ex.Message}");
            trace.AppendLine($"SaveChanges: FAILED - {ex.Message}");
            _db.ChangeTracker.Clear();
        }

        trace.AppendLine();
        trace.AppendLine(new string('=', 60));
        trace.AppendLine($"Final: checked {result.SeriesChecked} series, re-linked {result.SeriesAutoLinked}, backfilled {result.CoversBackfilled} cover(s) ({backfilledFromMetron} from Metron, {backfilledFromGcd} from GCD fallback).");
        if (result.Added.Count > 0)
        {
            trace.AppendLine();
            trace.AppendLine("Re-linked / backfilled:");
            foreach (var line in result.Added) trace.AppendLine("  " + line);
        }
        if (result.Errors.Count > 0)
        {
            trace.AppendLine();
            trace.AppendLine("Errors:");
            foreach (var line in result.Errors) trace.AppendLine("  " + line);
        }
        var rescanTraceText = trace.ToString();
        result.Trace = rescanTraceText;
        DebugLog.WriteCheckNowLog(rescanTraceText);

        return result;
    }

    // How long LOCG's own robots.txt asks every crawler to wait between
    // requests (Crawl-delay: 30.00) - this is the one real, documented
    // constraint on this whole feature, so it's a named constant instead
    // of a bare literal buried in a loop.
    private static readonly TimeSpan LocgCrawlDelay = TimeSpan.FromSeconds(30);

    public AutoFetchResult BackfillYearFromLocg(int year, Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"LOCG backfill for {year} run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        try
        {
            RunBackfillYearFromLocg(year, result, trace, onProgress);
            return result;
        }
        finally
        {
            trace.AppendLine();
            trace.AppendLine(new string('=', 60));
            trace.AppendLine($"Final: added {result.NewIssuesAdded} new issue(s) for {year}.");
            if (result.Added.Count > 0)
            {
                trace.AppendLine();
                trace.AppendLine("Added:");
                foreach (var line in result.Added) trace.AppendLine("  " + line);
            }
            if (result.Errors.Count > 0)
            {
                trace.AppendLine();
                trace.AppendLine("Errors:");
                foreach (var line in result.Errors) trace.AppendLine("  " + line);
            }
            var locgTraceText = trace.ToString();
            result.Trace = locgTraceText;
            DebugLog.WriteCheckNowLog(locgTraceText);
        }
    }

    // Walks every Monday of the given year (LOCG's weekly pages are
    // always Monday-to-Monday - see ILocgService) from Jan 1 through
    // Dec 31, pulling each week from LOCG and feeding it through the
    // exact same grouping/creation logic ComicList and GCD already use
    // (AddEntriesAsIssues) so a LOCG-sourced issue is handled identically
    // to one from any other source once it's in ComicListEntry shape.
    // One week failing (a transient site error, LOCG's own quota window
    // never opening up within LocgService's retry budget) is recorded
    // and skipped rather than aborting the whole run - since LOCG's AJAX
    // endpoint only actually lets a request through every few minutes
    // regardless of how this is spaced out, a full year can take several
    // hours in practice. Losing all of it over one stubborn week would be
    // far worse than a gap in the results that a second run (same year)
    // can simply fill back in, since already-known issues are
    // skipped/merged rather than duplicated either way.
    private void RunBackfillYearFromLocg(int year, AutoFetchResult result, System.Text.StringBuilder trace, Action<string>? onProgress = null)
    {
        var allSeries = _db.AllSeries.Include(s => s.Issues).ThenInclude(i => i.Covers).ToList();

        // Every Monday whose week (Monday-Sunday) overlaps this year,
        // collected up front - a plain index-based loop over a known list
        // is what lets "skip the delay after the very last week" below be
        // a simple i < count - 1 check instead of re-deriving "is this
        // the last iteration" from date arithmetic on the fly.
        var mondays = new List<DateOnly>();
        var firstDay = new DateOnly(year, 1, 1);
        // DayOfWeek: Sunday=0 ... Saturday=6, Monday=1 - walks backward
        // (never forward) from Jan 1 to land on that week's Monday, which
        // may fall in the prior December; LOCG's own week containing
        // Jan 1 is whichever Monday-Sunday span Jan 1 falls inside, same
        // as the site itself would show.
        var monday = firstDay.AddDays(-(((int)firstDay.DayOfWeek + 6) % 7));
        while (monday.Year == year || monday.AddDays(6).Year == year)
        {
            mondays.Add(monday);
            monday = monday.AddDays(7);
        }

        for (var i = 0; i < mondays.Count; i++)
        {
            var week = mondays[i];
            string progressLine;
            try
            {
                var entries = _locg.GetWeekEntries(week);
                var beforeCount = result.NewIssuesAdded;
                AddEntriesAsIssues(allSeries, entries, week, "LOCG", result);
                var added = result.NewIssuesAdded - beforeCount;
                progressLine = $"Week {i + 1}/{mondays.Count} ({week:MM/dd/yyyy}): {entries.Count} entries from LOCG, +{added} new issue(s).";
                trace.AppendLine(progressLine);
                _db.SaveChanges(); // persist incrementally - a later week's failure shouldn't lose earlier weeks' work
            }
            catch (Exception ex)
            {
                progressLine = $"Week {i + 1}/{mondays.Count} ({week:MM/dd/yyyy}): FAILED - {ex.Message}";
                result.Errors.Add($"LOCG week of {week:MM/dd/yyyy}: {ex.Message}");
            }

            // So a caller watching this run (NewBooks.razor's status
            // line) can tell it's actually making progress rather than
            // hung - this is now a multi-hour operation (LOCG's own
            // quota, plus a detail-page fetch per brand-new issue/cover -
            // see EnrichIssueDetails), long enough that silence until the
            // very end would be indistinguishable from stuck.
            onProgress?.Invoke(progressLine);
            // Also flushed to disk every week, not just at the very end -
            // readable even if the browser tab/circuit that started this
            // run gets navigated away from or closed (see RescanExisting-
            // Books' own SaveChanges-at-the-end risk for why that
            // matters: this run's progress shouldn't only ever live in
            // one in-memory StringBuilder).
            DebugLog.WriteCheckNowLog(trace.ToString());

            // No delay after the very last week - nothing left to be
            // polite to LOCG about once the run is finished.
            if (i < mondays.Count - 1)
                Thread.Sleep(LocgCrawlDelay);
        }

        trace.AppendLine($"Pulled {mondays.Count} week(s) from LOCG for {year}.");
    }

    public AutoFetchResult CheckForNewIssuesViaMetron(DateOnly after, DateOnly before, Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Metron discovery for {after:yyyy-MM-dd} .. {before:yyyy-MM-dd} run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        try
        {
            RunCheckForNewIssuesViaMetron(after, before, result, trace, onProgress);
            return result;
        }
        finally
        {
            trace.AppendLine();
            trace.AppendLine(new string('=', 60));
            trace.AppendLine($"Final: added {result.NewIssuesAdded} new issue(s).");
            if (result.Added.Count > 0)
            {
                trace.AppendLine();
                trace.AppendLine("Added:");
                foreach (var line in result.Added) trace.AppendLine("  " + line);
            }
            if (result.Errors.Count > 0)
            {
                trace.AppendLine();
                trace.AppendLine("Errors:");
                foreach (var line in result.Errors) trace.AppendLine("  " + line);
            }
            var metronTraceText = trace.ToString();
            result.Trace = metronTraceText;
            DebugLog.WriteCheckNowLog(metronTraceText);
        }
    }

    // Walks [after, before] in weekly chunks against Metron's own
    // GetIssuesInDateRange and feeds each chunk through the same
    // AddEntriesAsIssues logic every other source uses. Chunked purely
    // so one bad week can't lose a whole multi-month run and so progress
    // reports at the same granularity BackfillYearFromLocg's callers are
    // already used to - Metron's own list endpoint could just as well
    // handle the entire range in one paginated call, unlike LOCG's
    // genuinely weekly-only page.
    private void RunCheckForNewIssuesViaMetron(DateOnly after, DateOnly before, AutoFetchResult result, System.Text.StringBuilder trace, Action<string>? onProgress = null)
    {
        var allSeries = _db.AllSeries.Include(s => s.Issues).ThenInclude(i => i.Covers).ToList();

        var chunks = new List<(DateOnly Start, DateOnly End)>();
        var cursor = after;
        while (cursor <= before)
        {
            var end = cursor.AddDays(6);
            if (end > before) end = before;
            chunks.Add((cursor, end));
            cursor = end.AddDays(1);
        }
        if (chunks.Count == 0) chunks.Add((after, before));

        for (var i = 0; i < chunks.Count; i++)
        {
            var (start, end) = chunks[i];
            string progressLine;
            try
            {
                var entries = _metron.GetIssuesInDateRange(start, end);
                var beforeCount = result.NewIssuesAdded;
                AddEntriesAsIssues(allSeries, entries, start, "MTR", result);
                var added = result.NewIssuesAdded - beforeCount;
                progressLine = $"Week {i + 1}/{chunks.Count} ({start:MM/dd/yyyy}-{end:MM/dd/yyyy}): {entries.Count} issue(s) from Metron, +{added} new issue(s).";
                trace.AppendLine(progressLine);
                _db.SaveChanges(); // persist incrementally - a later chunk's failure shouldn't lose earlier chunks' work
            }
            catch (Exception ex)
            {
                progressLine = $"Week {i + 1}/{chunks.Count} ({start:MM/dd/yyyy}-{end:MM/dd/yyyy}): FAILED - {ex.Message}";
                result.Errors.Add($"Metron {start:MM/dd/yyyy}-{end:MM/dd/yyyy}: {ex.Message}");
            }

            onProgress?.Invoke(progressLine);
            DebugLog.WriteCheckNowLog(trace.ToString());
        }

        trace.AppendLine($"Pulled {chunks.Count} week-chunk(s) from Metron for {after:yyyy-MM-dd} .. {before:yyyy-MM-dd}.");
    }

    // A second pass over issues Metron already added whose Cover A still
    // shows a generated placeholder barcode and $0 price - i.e. the
    // per-issue detail call failed when the issue was first created and
    // never got retried, since BuildCoversFromEntry/AddMissingCoverVariants
    // only ever enrich a BRAND NEW cover. This is what a store actually
    // needs after a bug like 2026-11-22's DRF decimal-as-string one (now
    // fixed - see MetronService.JsonOptions): every issue added during
    // the outage window is stuck exactly like this forever unless
    // something explicitly goes back for it. Also picks up any cover
    // variants Metron has on file that a Check Now run never saw at all,
    // since variant expansion (ComicListEntry.Variants) only exists on
    // this same per-issue detail call too. Never run automatically -
    // same "store initiates this, not every Check Now" spirit as
    // RescanExistingBooks, since it's an explicit one-time catch-up
    // pass, not routine discovery.
    //
    // Matches on the "MTR-" placeholder barcode, not Issue.MetronIssueId -
    // that column didn't exist until 2026-11-23, so every issue added
    // during the actual outage (barcode-prefix "MTR", the whole reason
    // this method exists) has it null. For those, FindIssueId re-finds
    // the same issue on Metron by series name + number first (the exact
    // search AddEntriesAsIssues/Check Now itself never ran for this issue
    // because it was matched on MetronSeriesId/name instead) before the
    // usual per-id detail call can happen at all.
    public AutoFetchResult RetryMetronEnrichment(Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();

        var candidates = _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .ToList()
            .Where(i => i.Covers.Any(c => c.Price == 0 && c.Barcode.StartsWith("MTR-")))
            .ToList();

        onProgress?.Invoke($"Found {candidates.Count} Metron-sourced issue(s) still showing placeholder price/barcode.");

        var done = 0;
        foreach (var issue in candidates)
        {
            done++;
            var seriesName = issue.Series.Name;
            try
            {
                if (!issue.MetronIssueId.HasValue)
                {
                    // Added before Issue.MetronIssueId was persisted at
                    // all (the exact outage window this method exists
                    // for) - re-find it by the same series_name+number
                    // search Check Now originally used to match this
                    // issue against Metron in the first place.
                    var foundId = _metron.FindIssueId(seriesName, issue.IssueNumber, issue.ReleaseDate);
                    if (!foundId.HasValue)
                    {
                        result.Errors.Add($"{seriesName} #{issue.IssueNumber:0.##}: couldn't re-find this issue on Metron by series name/number - skipped.");
                        continue;
                    }
                    issue.MetronIssueId = foundId;
                }

                var tempEntry = new ComicListEntry { MetronIssueId = issue.MetronIssueId };
                _metron.EnrichIssueDetails(tempEntry);

                var staleCover = issue.Covers.FirstOrDefault(c => c.Price == 0 && c.Barcode.StartsWith("MTR-"));
                if (staleCover != null)
                {
                    if (!string.IsNullOrWhiteSpace(tempEntry.Upc)) staleCover.Barcode = tempEntry.Upc!;
                    if (tempEntry.Price.HasValue) staleCover.Price = tempEntry.Price.Value;
                    if (!string.IsNullOrWhiteSpace(tempEntry.Description)) staleCover.Description = tempEntry.Description;
                }
                if (!string.IsNullOrWhiteSpace(tempEntry.Publisher) && string.IsNullOrWhiteSpace(issue.Publisher))
                    issue.Publisher = tempEntry.Publisher;

                // Reuses AddMissingCoverVariants' exact dedup logic to add
                // any variant covers this issue was missing - clearing
                // MetronIssueId first so BuildCoversFromEntry (called
                // inside it) doesn't pay for a second, redundant detail
                // call for data tempEntry already carries from the one
                // above.
                var variantsFound = tempEntry.Variants.Count;
                tempEntry.MetronIssueId = null;
                AddMissingCoverVariants(issue, new List<ComicListEntry> { tempEntry }, seriesName, "MTR", result);

                result.Added.Add($"{seriesName} #{issue.IssueNumber:0.##}: re-enriched from Metron ({variantsFound} variant(s) on file there).");
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{seriesName} #{issue.IssueNumber:0.##}: retry enrichment failed: {ex.Message}");
            }

            if (done % 5 == 0 || done == candidates.Count)
            {
                _db.SaveChanges();
                var line = $"Re-enriched {done}/{candidates.Count} issue(s) from Metron.";
                trace.AppendLine(line);
                onProgress?.Invoke(line);
                DebugLog.WriteCheckNowLog(trace.ToString());
            }
        }

        _db.SaveChanges();
        if (trace.Length == 0) trace.AppendLine("No issues needed retrying.");
        result.Trace = trace.ToString();
        return result;
    }

    // Scans every cover image already on file, groups them by the
    // downloaded file's own byte content (SHA256), and clears
    // CoverImagePath back to null for any cover whose image matches a
    // hash that turns up across too many DIFFERENT issues to plausibly
    // be real, unique art - a source's own generic "cover not ready yet"
    // stock graphic, not something a null/empty CoverImagePath check
    // could ever catch (confirmed live: a real, non-blank image file,
    // just the wrong one - a "Cover Coming Soon" graphic baked into the
    // picture itself). Purely local file reads - no Comic Vine calls, no
    // rate-limit exposure - so this is cheap enough to run at the start
    // of every RescanExistingBooks, not something that needs its own
    // separate button or schedule.
    //
    // A newly-confirmed hash is remembered in PlaceholderImageRegistry so
    // ComicVineService.DownloadCoverImage can reject it immediately on
    // every future download, before ever saving it as if it were real -
    // this scan only has to catch each distinct placeholder graphic
    // once, not every single run.
    public int ClearPlaceholderCovers()
    {
        // Real art repeating across this many DIFFERENT issues is
        // implausible outside a few known legitimate cases (a reprint
        // reusing an old cover, a handful of variants sharing art) - a
        // generic "not ready yet" stock graphic repeating this often is
        // the common, expected case for a source that serves one.
        const int placeholderThreshold = 5;

        var coversWithImages = _db.Covers
            .Include(c => c.Issue)
            .Where(c => c.CoverImagePath != null && c.CoverImagePath != "")
            .ToList();

        var coversByHash = new Dictionary<string, List<Cover>>();
        var issueIdsByHash = new Dictionary<string, HashSet<int>>();

        foreach (var cover in coversWithImages)
        {
            string hash;
            try
            {
                if (!File.Exists(cover.CoverImagePath)) continue;
                using var stream = File.OpenRead(cover.CoverImagePath);
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            }
            catch
            {
                continue; // unreadable file - leave it alone rather than guess
            }

            if (!coversByHash.TryGetValue(hash, out var coverList))
            {
                coverList = new List<Cover>();
                coversByHash[hash] = coverList;
            }
            coverList.Add(cover);

            if (!issueIdsByHash.TryGetValue(hash, out var issueIds))
            {
                issueIds = new HashSet<int>();
                issueIdsByHash[hash] = issueIds;
            }
            issueIds.Add(cover.IssueId);
        }

        var cleared = 0;
        foreach (var (hash, issueIds) in issueIdsByHash)
        {
            if (issueIds.Count < placeholderThreshold) continue;

            PlaceholderImageRegistry.Add(hash);
            foreach (var cover in coversByHash[hash])
            {
                cover.CoverImagePath = null;
                cleared++;
            }
        }

        if (cleared > 0) _db.SaveChanges();
        return cleared;
    }

    // Called from RescanExistingBooks (for a series that still has a
    // missing cover) AND from RunCheckForNewIssues's own per-series loop
    // (for every already-linked series whose RecheckCooldown has expired)
    // - the latter was added specifically because a series whose covers
    // are already all filled in never shows up in Re-scan's candidate
    // list at all, so without this it could never get a second look once
    // RescanExistingBooks stopped visiting it (confirmed live: Wonder
    // Woman stayed linked to the wrong volume indefinitely for exactly
    // this reason). The extra SearchVolumes call this adds to a normal
    // Check Now is bounded by RecheckCooldown (currently 3h) the same way
    // CheckOneSeries's own calls already are, so it's one more call per
    // series per cooldown window, not per run. This exists specifically to
    // undo damage from a link made before PickBestVolumeMatch's
    // issue-count-based matching existed (the "Superman (1939)" bug).
    private void ReverifyVolumeLink(Series series, AutoFetchResult result)
    {
        if (series.Issues.Count == 0) return; // nothing to judge a better match against

        List<ComicVineVolumeMatch> matches;
        try
        {
            matches = _comicVine.SearchVolumes(series.Name);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{series.Name}: {ex.Message}");
            if (IsRateLimitError(ex)) throw;
            return;
        }

        var best = PickBestVolumeMatch(matches, series.Name, series.StartYear, series.Issues);
        if (best == null) return; // no confident match at all

        // Sync the display year to Comic Vine's own record for whichever
        // volume we're confident is the right one - even when the link
        // itself doesn't need to change below, a series created with a
        // wrong placeholder StartYear (see SyncStartYear's own comment)
        // still gets corrected here every time this runs.
        SyncStartYear(series, best);

        if (best.Id == series.ComicVineVolumeId) return; // already linked to the right one

        var maxKnownIssue = series.Issues.Max(i => i.IssueNumber);
        var current = matches.FirstOrDefault(m => m.Id == series.ComicVineVolumeId);

        // Only swap when the new candidate is a clearly better numeric fit.
        // Comic Vine's own search doesn't always include the currently-
        // linked volume among its results (a volume can drop out of a name
        // search for reasons that have nothing to do with being wrong), so
        // "the current link wasn't found in this search" is never treated
        // as "the current link is wrong" on its own - only a genuinely
        // better-fitting alternative is.
        //
        // The old check here only asked "does the CURRENT link still
        // technically cover the known issue numbers" - nearly always true
        // for a decades-old legacy volume with hundreds of issues, so a
        // bad link could never self-correct on a later Re-scan (confirmed
        // live: Wonder Woman stayed linked to the original 1942 volume
        // through every Re-scan since its 329 issues trivially satisfied
        // ">= maxKnownIssue" every single time). This asks the real
        // question instead - is CURRENT at least as good a fit as BEST, by
        // the same smallest-qualifying-count rule PickBestVolumeMatch
        // itself uses - so a clearly-too-big legacy volume loses to a
        // smaller, better-fitting candidate instead of being permanently
        // grandfathered in just for still being numerically possible.
        var bestFits = VolumeCoversIssue(best, maxKnownIssue);
        if (!bestFits) return; // the candidate doesn't even cover the known issues - never worth switching to

        var currentFits = current != null && VolumeCoversIssue(current, maxKnownIssue);
        if (currentFits && current!.IssueCount <= best.IssueCount) return; // current is already at least as good a fit - don't flap to an equally-valid alternative

        result.Added.Add($"{series.Name}: re-linked from Comic Vine volume {series.ComicVineVolumeId} to {best.Id} (\"{best.Name}\", {best.IssueCount} issue(s) on file - a better fit for issue #{maxKnownIssue:0.##} than the previous link).");
        series.ComicVineVolumeId = best.Id;
        // Forces a full GetIssuesInRange re-check against the corrected
        // volume on the very next normal Check Now, instead of waiting out
        // RecheckCooldown against what was, until a moment ago, the wrong
        // volume entirely.
        series.LastCheckedForNewIssues = null;
        result.SeriesAutoLinked++;

        // The wrong volume may already have supplied cover art for issues
        // on file (confirmed live: Batman #17 had all 7 of its covers
        // filled in with a single Golden-Age image pulled from the wrong,
        // just-corrected volume) - re-linking alone doesn't touch already-
        // downloaded images, so without this a corrected link would still
        // display the old, wrong art forever. Clearing every cover here
        // means a FEW correct Metron/GCD-sourced covers get needlessly
        // re-fetched too (there's no stored record of which backfill
        // source actually supplied a given image), but that's a cheap,
        // harmless re-download next Re-scan - leaving a wrong cover in
        // place is the far worse outcome.
        foreach (var issue in series.Issues)
        foreach (var cover in issue.Covers)
            cover.CoverImagePath = null;
    }

    // Comic-Vine-only half of what RescanSeriesCovers used to do in one
    // piece: looks up every issue number already on file for this series
    // (not just whatever falls inside CheckOneSeries' normal rolling
    // few-months discovery window) and backfills any that are still
    // missing cover art. Can throw on a rate limit - RescanExistingBooks
    // decides what that means for the rest of the run (skip Comic Vine
    // from here on, keep going with GCD).
    private void BackfillFromComicVine(Series series, AutoFetchResult result)
    {
        // GetIssuesInRange needs a date range - widened here to cover the
        // earliest to latest release date already on file for this
        // series (falling back to a generous 5-year window if none of its
        // issues have a release date at all), instead of reusing
        // DiscoveryWindow()'s narrow current-quarter slice.
        var knownDates = series.Issues.Where(i => i.ReleaseDate != null).Select(i => i.ReleaseDate!.Value).ToList();
        var since = knownDates.Count > 0 ? knownDates.Min().AddDays(-7) : DateOnly.FromDateTime(DateTime.Today.AddYears(-5));
        var until = DateOnly.FromDateTime(DateTime.Today.AddMonths(3));

        List<ComicVineNewIssue> cvIssues;
        try
        {
            cvIssues = _comicVine.GetIssuesInRange(series.ComicVineVolumeId!.Value, since, until);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{series.Name}: {ex.Message}");
            if (IsRateLimitError(ex)) throw;
            return;
        }

        foreach (var cvIssue in cvIssues)
            BackfillCoverImage(series, cvIssue, result);
    }

    // GCD/Metron need a real date to tell same-named volumes apart
    // (Wonder Woman, Batman, Green Lantern Corps and plenty of other
    // characters have been relaunched under the same title many times
    // across decades, sometimes reusing the exact same issue number).
    // series.StartYear is NOT reliable for this - it's one scalar for
    // the whole series, often wrong (a creation-time placeholder, or the
    // wrong Comic Vine volume's earliest-appearance year - confirmed
    // live: Wonder Woman's series.StartYear was 1942, which sent Metron
    // looking for a 1940s issue number 39 and it happily returned the
    // real 1948 Golden Age cover). Using the issue's own release YEAR
    // alone turned out not to be enough either - confirmed live, same
    // session: Metron/GCD's "what year did this volume start" filters
    // still came back with nothing for a correctly-dated modern issue,
    // because "the volume's start year" and "this issue's release year"
    // are different numbers for any series more than one year old. The
    // fix living in MetronService/GcdService now is to stop asking
    // either source to filter by year at all, and instead compare each
    // candidate's own cover/publication date against this issue's real
    // ReleaseDate/FocDate client-side - which needs the full date, not
    // just a year, hence DateOnly here. series.StartYear only backs a
    // rough mid-year guess for an issue with no date on file at all.
    private static DateOnly? IssueDateHint(Series series, Issue issue)
    {
        if (issue.ReleaseDate != null) return issue.ReleaseDate;
        if (issue.FocDate != null) return issue.FocDate;
        return series.StartYear > 0 ? new DateOnly(series.StartYear, 6, 15) : null;
    }

    // One issue at a time - GCD is a free, unauthenticated, rate-limited
    // API (see GcdService's own header comment), so this is only ever
    // reached from the manual, deliberate Re-scan action, never the
    // routine automatic Check Now. Returns the full lookup result (not
    // just success/fail) so the caller can tally WHY a miss happened.
    private GcdCoverLookupResult BackfillCoverFromGcd(Series series, Issue issue)
    {
        var missingCovers = issue.Covers.Where(c => string.IsNullOrWhiteSpace(c.CoverImagePath)).ToList();
        if (missingCovers.Count == 0)
            return new GcdCoverLookupResult { Reason = "no missing cover to fill" };

        var hint = $"{series.Name}_{issue.IssueNumber:0.##}_{Guid.NewGuid():N}".Replace(" ", "_");
        var expectedDate = IssueDateHint(series, issue);
        var result = _gcd.FindAndDownloadCover(series.Name, issue.IssueNumber, expectedDate, hint, missingCovers.Count);
        if (result.Success)
            AssignCoversByLetter(missingCovers, result.AllCovers);
        return result;
    }

    // Same shape as BackfillCoverFromGcd, one issue at a time, against
    // Metron instead - see MetronService/IMetronService for why this
    // exists and its own rate-limit/bot-protection handling.
    private MetronCoverLookupResult BackfillCoverFromMetron(Series series, Issue issue)
    {
        var missingCovers = issue.Covers.Where(c => string.IsNullOrWhiteSpace(c.CoverImagePath)).ToList();
        if (missingCovers.Count == 0)
            return new MetronCoverLookupResult { Reason = "no missing cover to fill" };

        var hint = $"{series.Name}_{issue.IssueNumber:0.##}_{Guid.NewGuid():N}".Replace(" ", "_");
        var expectedDate = IssueDateHint(series, issue);
        var result = _metron.FindAndDownloadCover(series.Name, issue.IssueNumber, expectedDate, hint, missingCovers.Count);
        if (result.Success)
            AssignCoversByLetter(missingCovers, result.AllCovers);
        return result;
    }

    // Found live this session: every missing Cover row for one issue (A,
    // B, C, D...) was getting the exact same downloaded image, because
    // the old code just looped every missing cover and wrote the same
    // single path to each - even though Metron's own site shows genuinely
    // distinct art per variant (confirmed via screenshot: Wonder Woman
    // #39's own "Covers (5)" panel). FindAndDownloadCover/GCD's
    // equivalent now return one CoverCandidate per distinct image they
    // actually found (see CoverCandidate on IMetronService) - this maps
    // them onto ComicPos's own Cover rows by best-effort letter matching:
    // a candidate whose Label parses to a letter ("Cover B" -> 'B') goes
    // to the Cover row with that exact CoverLetter; anything left over
    // (an unlettered variant name, or more/fewer candidates than Cover
    // rows) gets handed out to whatever Cover rows are still unmatched,
    // in order - a real distinct cover for every row beats a blank one,
    // even where the exact letter pairing can't be confirmed.
    private static void AssignCoversByLetter(List<Cover> missingCovers, List<CoverCandidate> candidates)
    {
        if (candidates.Count == 0) return;

        var remainingCovers = new List<Cover>(missingCovers);
        var leftoverCandidates = new List<CoverCandidate>();

        foreach (var candidate in candidates)
        {
            var letter = ParseCoverLetterFromLabel(candidate.Label);
            var match = letter != null
                ? remainingCovers.FirstOrDefault(c => string.Equals(c.CoverLetter?.Trim(), letter, StringComparison.OrdinalIgnoreCase))
                : null;

            if (match != null)
            {
                match.CoverImagePath = candidate.ImagePath;
                remainingCovers.Remove(match);
            }
            else
            {
                leftoverCandidates.Add(candidate);
            }
        }

        // Whatever didn't get a clean letter match (an unlettered variant
        // name, a label that parsed to a letter no remaining Cover row
        // actually has, or simply more candidates than rows) still goes
        // out to whatever Cover rows are left, in order, rather than
        // being thrown away.
        foreach (var candidate in leftoverCandidates)
        {
            if (remainingCovers.Count == 0) break;
            remainingCovers[0].CoverImagePath = candidate.ImagePath;
            remainingCovers.RemoveAt(0);
        }
    }

    // "Cover B", "2nd Printing Cover C Variant" -> "B"/"C". No label at
    // all (the primary/base cover, Label == null) is treated as "A" -
    // the overwhelmingly common convention for which letter a comic's
    // un-suffixed main cover is filed under in this app.
    private static string? ParseCoverLetterFromLabel(string? label)
    {
        if (label == null) return "A";
        var match = Regex.Match(label, @"Cover\s+([A-Za-z])\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    // Builds and saves the Issue+Cover for one Comic Vine issue, if its
    // issue number isn't already known for this series. Shared by the
    // per-series check and the new-#1 discovery pass below so both add
    // issues the exact same way.
    private void AddIssueIfNew(Series series, ComicVineNewIssue cvIssue, HashSet<float> knownNumbers, AutoFetchResult result)
    {
        if (knownNumbers.Contains(cvIssue.IssueNumber)) return;

        var issue = new Issue
        {
            Series = series,
            IssueNumber = cvIssue.IssueNumber,
            Publisher = "",
            Type = IssueType.SingleIssue,
            ReleaseDate = cvIssue.StoreDate,
            // Comic Vine has no FOC date, only street date - 3 weeks
            // before is a common distributor rule of thumb, not a fact.
            FocDate = cvIssue.StoreDate?.AddDays(-21)
        };

        string? imagePath = null;
        if (!string.IsNullOrWhiteSpace(cvIssue.ImageUrl))
        {
            var hint = $"{series.Name}_{cvIssue.IssueNumber:0.##}_{Guid.NewGuid():N}".Replace(" ", "_");
            imagePath = _comicVine.DownloadCoverImage(cvIssue.ImageUrl, hint);
        }

        var cover = new Cover
        {
            CoverLetter = "A",
            // Not a real UPC - just needs to be unique. The store
            // overwrites it with the real barcode once the item arrives.
            Barcode = $"CV-{Guid.NewGuid():N}",
            Price = 0,
            StockQuantity = 0,
            CoverImagePath = imagePath,
            Description = string.IsNullOrWhiteSpace(cvIssue.Name) ? null : cvIssue.Name
        };

        issue.Covers.Add(cover);
        _db.Issues.Add(issue);
        knownNumbers.Add(cvIssue.IssueNumber);

        result.NewIssuesAdded++;
        var streetInfo = cvIssue.StoreDate != null ? $" (street {cvIssue.StoreDate:MM/dd/yyyy})" : "";
        result.Added.Add($"{series.Name} #{cvIssue.IssueNumber:0.##}{streetInfo}");
    }

    // Finds brand-new series the store doesn't carry yet and are worth
    // showing in New Books. Two strategies, depending on whether the store
    // has told us which publishers it cares about (Settings > Tracked
    // Publishers):
    //
    //  - Publishers set: a targeted, per-publisher search for volumes that
    //    started this year or next (DiscoverByTrackedPublishers). Cheap and
    //    relevant - every lookup is for a publisher the store actually
    //    wants, and once a volume's found, every issue it has in the
    //    window gets added (not just #1), so an ongoing new series is
    //    picked up fully, not just its launch issue.
    //  - No publishers set: a global search across every #1 issue on Comic
    //    Vine regardless of publisher (DiscoverGlobalNumberOnes). This
    //    casts the widest possible net, but Comic Vine's catalog is
    //    overwhelmingly manga/foreign/small-press for a typical US shop -
    //    expect a lot of noise, and setting Tracked Publishers is strongly
    //    recommended once the store knows which publishers it stocks.
    // Pulls comiclist.info's real weekly list (see ComicListService) and
    // adds any single-issue title the store doesn't already carry. Entries
    // are grouped by (series name, issue number) first, since the source
    // list has one line per variant cover - so a title with 5 covers
    // becomes one Issue with 5 Covers here, not 5 separate issues. If
    // Tracked Publishers is set, only matching publishers are kept, same
    // filter DiscoverByTrackedPublishers applies to its Comic Vine search -
    // otherwise every publisher on the list is included (this source is
    // already real/current, not a noisy global catalog scan, so skipping
    // the filter when unset is reasonable).
    private void DiscoverFromComicList(List<Series> allSeries, AutoFetchResult result)
    {
        ComicListWeek week;
        try
        {
            week = _comicList.GetThisWeeksMasterList();
        }
        catch (Exception ex)
        {
            // Unreachable, blocked, or layout changed - see
            // ComicListService. The message is specific (which step
            // failed and why), not just "it didn't work", so this is
            // diagnosable from the Check Now result alone.
            result.Errors.Add($"ComicList: {ex.Message}");
            return;
        }

        AddEntriesAsIssues(allSeries, week.Entries, week.ReleaseDate, "CL", result);
    }

    // comiclist.info's per-publisher "Extended Forecast" posts - real
    // forward-looking solicitation data (roughly 10 months out), unlike
    // the weekly master list above which only shows what's shipping THIS
    // week (by which point FOC already passed 3 weeks ago). This is what
    // actually makes an FOC buy report useful instead of always-too-late.
    // See ComicListService.GetExtendedForecastEntries.
    private void DiscoverFromExtendedForecast(List<Series> allSeries, AutoFetchResult result)
    {
        List<ComicListEntry> entries;
        try
        {
            entries = _comicList.GetExtendedForecastEntries();
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Extended Forecast: {ex.Message}");
            return;
        }

        // The "default" date here is only a fallback AddEntriesAsIssues
        // would use if an entry were somehow missing its own ReleaseDate -
        // shouldn't happen for this source, since every Extended Forecast
        // entry carries one (see ParseExtendedForecastEntries).
        AddEntriesAsIssues(allSeries, entries, DateOnly.FromDateTime(DateTime.Today), "CLF", result);
    }

    // comicade.com's FOC-cutoff catalogue - see ComicadeFocService and
    // IComicadeFocService for what makes this worth a separate source from
    // ComicList: every entry here carries an exact FOC date, real UPC,
    // real price and a real cover image URL already, instead of the
    // estimates/placeholders AddEntriesAsIssues otherwise falls back to.
    private void DiscoverFromComicadeFoc(List<Series> allSeries, AutoFetchResult result)
    {
        List<ComicListEntry> entries;
        try
        {
            entries = _comicadeFoc.GetUpcomingFocEntries();
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Comicade FOC: {ex.Message}");
            return;
        }

        // Same fallback-date caveat as Extended Forecast above - every
        // entry from this source carries its own ReleaseDate, so this
        // default is never actually used in practice.
        AddEntriesAsIssues(allSeries, entries, DateOnly.FromDateTime(DateTime.Today), "CFC", result);
    }

    // Grand Comics Database (comics.org) - see GcdService and IGcdService
    // for what this can and can't give us. Shares the same grouping/
    // creation logic as ComicList via AddEntriesAsIssues below, since both
    // sources return the same ComicListEntry shape; only the barcode
    // prefix differs, purely so a placeholder barcode's source is obvious
    // if it ever needs debugging later.
    private void DiscoverFromGcd(List<Series> allSeries, AutoFetchResult result)
    {
        ComicListWeek week;
        try
        {
            week = _gcd.GetThisWeeksOnSaleIssues();
        }
        catch (Exception ex)
        {
            result.Errors.Add($"GCD: {ex.Message}");
            return;
        }

        AddEntriesAsIssues(allSeries, week.Entries, week.ReleaseDate, "GCD", result);
    }

    // Shared by DiscoverFromComicList and DiscoverFromGcd: groups raw
    // entries by (series name, issue number) - a title with several
    // variant covers becomes one Issue with several Covers, not several
    // Issues - then creates whatever Series/Issue/Cover rows don't already
    // exist. Applies the Tracked Publishers filter (same rule
    // DiscoverByTrackedPublishers uses for its Comic Vine search) when
    // one's set; otherwise keeps everything, since both sources already
    // return real current-week data rather than a noisy global catalog.
    private void AddEntriesAsIssues(List<Series> allSeries, List<ComicListEntry> rawEntries, DateOnly releaseDate, string barcodePrefix, AutoFetchResult result)
    {
        var trackedPublishers = (_storeSettings.Load().TrackedPublishers ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var entries = rawEntries.Where(e => e.SeriesName != null && e.IssueNumber != null);
        if (trackedPublishers.Count > 0)
        {
            // A blank Publisher passes through untouched rather than
            // getting filtered out - Metron's own weekly discovery call
            // (GetIssuesInDateRange) deliberately doesn't carry Publisher
            // at all (that field only exists on Metron's per-issue DETAIL
            // response, and paying for a detail call on every single
            // industry-wide result just to filter it would undo the
            // whole point of that call being cheap - see IMetronService).
            // Silently excluding every Metron-sourced entry whenever a
            // store has TrackedPublishers configured would break
            // discovery entirely for anyone using that setting, so an
            // unknown publisher is treated as "don't know yet, let it
            // through" rather than "assume not tracked."
            entries = entries.Where(e => string.IsNullOrWhiteSpace(e.Publisher) || trackedPublishers.Any(p =>
                e.Publisher.Contains(p, StringComparison.OrdinalIgnoreCase)));
        }

        var groups = entries.GroupBy(e => (e.SeriesName!.Trim(), e.IssueNumber!.Value));

        // Grouped/looked-up by a NORMALIZED key, not the raw name - a
        // leading "The " is the one difference between two names that
        // are, in every way that matters here, the same series, and
        // different sources disagree on whether they include it (one
        // week's source calls it "Flash", another calls it "The Flash")
        // with nothing else to tell them apart. Confirmed live: this
        // split "Flash" into two separate Series rows - "Flash" and "The
        // Flash" - that each separately auto-linked to a DIFFERENT wrong
        // Comic Vine volume and each carried their own (different) copy
        // of issue #39, neither aware the other existed. The stored
        // Series.Name itself is left exactly as first created (the
        // dictionary keeps whichever row was already in allSeries, or the
        // raw seriesName typed in for a genuinely new one) - only the
        // MATCHING key is normalized, so this never renames an existing,
        // already-linked series out from under itself.
        var seriesByName = allSeries
            .GroupBy(s => SeriesNameKey.Normalize(s.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Metron's own volume id is a precise identity with no name/year
        // guessing involved at all (see Series.MetronSeriesId) - checked
        // FIRST, ahead of the name-based dictionary above, whenever an
        // entry carries one. A series that predates Metron discovery
        // (created via LOCG/ComicList/GCD/Comicade/a manual add) simply
        // has no MetronSeriesId yet and falls through to the name-based
        // match below exactly as before, which then BACKFILLS this id
        // onto it so every later run matches by id from then on.
        var seriesByMetronId = allSeries
            .Where(s => s.MetronSeriesId.HasValue)
            .GroupBy(s => s.MetronSeriesId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var group in groups)
        {
            var (seriesName, issueNumber) = group.Key;
            var seriesKey = SeriesNameKey.Normalize(seriesName);
            var metronSeriesId = group.First().MetronSeriesId;
            var startYearHint = group.First().SeriesStartYearHint;

            Series? series = null;
            if (metronSeriesId.HasValue)
                seriesByMetronId.TryGetValue(metronSeriesId.Value, out series);
            if (series == null)
                seriesByName.TryGetValue(seriesKey, out series);

            if (series == null)
            {
                // StartYear used to be hardcoded to DateTime.Today.Year
                // for every brand-new series discovered from a weekly
                // scrape, regardless of how old the real series actually
                // is - a 1940s character's latest issue, discovered this
                // week, got stamped with THIS week's year as its "start
                // year." That placeholder then fed straight into
                // PickBestVolumeMatch's own StartYear fallback and could
                // coincidentally match an unrelated same-named volume
                // that really did start this year, which is exactly the
                // "pulling from the wrong series" bug class this is
                // fixing. Metron's own series.year_began (surfaced here
                // as SeriesStartYearHint) is the real answer whenever a
                // source provides it; falling back to this issue's own
                // release year is still a guess, but a far closer one
                // than "whatever year this Check Now run happens to be
                // running in" for sources that don't carry a real year
                // (LOCG/ComicList/GCD/Comicade).
                var fallbackYear = group.First().ReleaseDate?.Year ?? releaseDate.Year;
                series = new Series
                {
                    Name = seriesName,
                    StartYear = startYearHint ?? fallbackYear,
                    MetronSeriesId = metronSeriesId
                };
                _db.AllSeries.Add(series);
                seriesByName[seriesKey] = series;
                if (metronSeriesId.HasValue) seriesByMetronId[metronSeriesId.Value] = series;
                allSeries.Add(series); // so later discovery steps see it as already-known
            }
            else
            {
                // Backfill a precise identity and/or a real start year
                // onto a series this run now has better information
                // about, now that an authoritative source (Metron) has
                // actually weighed in on it - but never overwrite a
                // StartYear that Comic Vine has already independently
                // confirmed (ComicVineVolumeId set), since that value has
                // already survived PickBestVolumeMatch's own issue-count
                // validation and is more trustworthy than a bare year
                // number with nothing else checked against it.
                if (metronSeriesId.HasValue && series.MetronSeriesId != metronSeriesId)
                {
                    series.MetronSeriesId = metronSeriesId;
                    seriesByMetronId[metronSeriesId.Value] = series;
                }
                if (startYearHint is int y && y > 0 && series.StartYear != y && series.ComicVineVolumeId == null)
                    series.StartYear = y;
            }

            // Used to just skip the whole group here whenever the issue
            // number was already known - but a later week's scrape of the
            // SAME issue can know about MORE variant covers than an
            // earlier one did (incentive/retailer-exclusive covers in
            // particular often get announced after the initial solicit),
            // and skipping silently discarded those instead of adding
            // them. AddMissingCoverVariants adds whatever cover letters
            // aren't already on file for an issue that's already known;
            // only a genuinely brand-new issue number falls through to
            // the "create a new Issue" path below.
            var existingIssue = series.Issues.FirstOrDefault(i => i.IssueNumber == issueNumber);
            if (existingIssue != null)
            {
                AddMissingCoverVariants(existingIssue, group, seriesName, barcodePrefix, result);
                continue;
            }

            // A plain LINQ projection won't do here - downloading a cover
            // image (comicade.com entries only) is a side effect, and a
            // failed download for one variant shouldn't lose the others.
            // Each entry in the group can now expand into SEVERAL covers
            // (a Metron entry's own Variants list - see
            // BuildCoversFromEntry), so this collects every candidate
            // across the whole group first and only resolves final
            // letters once all of them are known - see
            // ResolveCoverLetters for why that matters (confirmed live:
            // Action Comics #1104 getting only Cover A from Metron, and
            // Ultimates #19 getting three/four different LOCG variants
            // all labeled Cover B, were the same underlying gap - no
            // step that both expanded every variant AND gave each one
            // its own non-colliding letter).
            var coverCandidates = new List<(Cover Cover, bool IsGuess)>();
            foreach (var e in group)
                coverCandidates.AddRange(BuildCoversFromEntry(e, seriesName, issueNumber, barcodePrefix, result));
            ResolveCoverLetters(coverCandidates, Enumerable.Empty<string>());
            var covers = coverCandidates.Select(c => c.Cover).ToList();

            // Extended Forecast/Comicade FOC entries each carry their own
            // ship date (they span weeks to months, not one shared week) -
            // use it when present, falling back to the shared week-level
            // date for sources that don't set it (the weekly master list,
            // GCD).
            var entryReleaseDate = group.First().ReleaseDate ?? releaseDate;

            // A real FOC date when the source gives one (comicade.com) -
            // the same "release minus 3 weeks" rule of thumb used
            // everywhere else otherwise (Comic Vine-sourced issues
            // included - see AddIssueIfNew).
            var entryFocDate = group.First().FocDate ?? entryReleaseDate.AddDays(-21);

            var issue = new Issue
            {
                Series = series,
                IssueNumber = issueNumber,
                Publisher = group.First().Publisher,
                // Kept around specifically so RetryMetronEnrichment can
                // come back and re-fetch this exact issue's detail call
                // later, for an issue that's already on file by the time
                // a fix like the 2026-11-22 decimal-parsing one lands -
                // see Issue.MetronIssueId's own doc comment.
                MetronIssueId = group.First().MetronIssueId,
                // Used to be hardcoded SingleIssue regardless of what the
                // scraped title actually is - a "Giant Days Volume 1 TP"
                // or "...Omnibus Volume 1 HC" solicitation got filed under
                // Single Issues in Inventory's browse-by-type screen right
                // alongside genuine single issues, which is wrong on its
                // own and is also exactly the same name pattern
                // TryLinkAsCollectedEdition now checks for auto-linking -
                // this keeps both reading the format off the same signal.
                Type = StripCollectedEditionSuffix(seriesName) != null ? IssueType.CollectedEdition : IssueType.SingleIssue,
                ReleaseDate = entryReleaseDate,
                FocDate = entryFocDate,
                Covers = covers
            };

            _db.Issues.Add(issue);

            result.NewIssuesAdded++;
            result.Added.Add($"{seriesName} #{issueNumber:0.##} ({covers.Count} cover(s), {entryReleaseDate:MM/dd/yyyy})");
        }
    }

    // Shared by both the "brand-new issue" path and the "issue already
    // known, just add any missing cover variants" path in
    // AddEntriesAsIssues - downloading a cover image (comicade.com
    // entries only) is a side effect that can fail independently per
    // variant, so this is its own method rather than inline in either
    // caller. Returns one or more candidate covers for this single
    // ComicListEntry: the entry's own primary cover, PLUS one more per
    // Metron "Variants" entry (see ComicListEntry.Variants) when this is
    // a Metron-sourced entry whose detail enrichment found any - Metron's
    // cheap list call only ever returns the primary/Cover-A edition, so
    // a brand-new Metron issue used to only ever get one cover no matter
    // how many the real book actually has (confirmed live: Action Comics
    // #1104 showed a real Cover A, B, C and E on Metron's own site but
    // only Cover A ever made it into this app). Each candidate comes
    // back paired with whether its letter is a confident read off the
    // source or a guess with no real letter behind it yet - the caller
    // (ResolveCoverLetters) is what turns that into a final, non-
    // colliding CoverLetter for every candidate in the batch.
    private List<(Cover Cover, bool IsGuess)> BuildCoversFromEntry(ComicListEntry e, string seriesName, float issueNumber, string barcodePrefix, AutoFetchResult result)
    {
        // LOCG's weekly list has no UPC or real FOC date at all - only
        // its own per-issue detail page does (see
        // ILocgService.EnrichIssueDetails) - so this is the one place
        // that fetches it, right before building the Cover row that
        // needs it. Only ever reached for a BRAND NEW issue/cover (both
        // call sites - the "new issue" path and AddMissingCoverVariants -
        // only call this for something not already on file), so an
        // already-known cover never pays for a repeat detail-page fetch.
        if (!string.IsNullOrWhiteSpace(e.DetailUrl))
        {
            try
            {
                _locg.EnrichIssueDetails(e);
            }
            catch (Exception ex)
            {
                // Best-effort (see ILocgService.EnrichIssueDetails) - a
                // failure here still leaves the issue/cover itself fully
                // usable, just without a real UPC/FOC date.
                result.Errors.Add($"{seriesName} #{issueNumber:0.##} ({e.CoverLetter}): LOCG detail page enrichment failed: {ex.Message}");
            }
        }
        else if (e.MetronIssueId.HasValue)
        {
            // Metron's own weekly discovery list (GetIssuesInDateRange)
            // already carries a release date, FOC date, and cover image
            // directly, cheaply, with no per-issue call at all - this one
            // extra detail-endpoint call is for the handful of fields
            // that list response doesn't have (UPC, price, publisher,
            // description) AND for every other cover Metron has on file
            // (e.Variants, populated by this same call - see
            // IMetronService.EnrichIssueDetails), same "only for a
            // brand-new issue/cover, never repeated" timing as LOCG's
            // enrichment above, just a lot cheaper per call (Metron's own
            // paced rate limit, ~3.5s, vs LOCG's 30s-per-issue crawl
            // delay) and with nothing to click through (no Cloudflare
            // check - Metron is a plain token-authenticated REST API).
            try
            {
                _metron.EnrichIssueDetails(e);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{seriesName} #{issueNumber:0.##} ({e.CoverLetter}): Metron detail enrichment failed: {ex.Message}");
            }
        }

        string? coverImagePath = null;
        if (!string.IsNullOrWhiteSpace(e.CoverImageUrl))
        {
            try
            {
                var hint = $"{seriesName}_{issueNumber:0.##}_{e.CoverLetter}_{Guid.NewGuid():N}".Replace(" ", "_");
                coverImagePath = _comicVine.DownloadCoverImage(e.CoverImageUrl, hint);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{seriesName} #{issueNumber:0.##} ({e.CoverLetter}): cover image download failed: {ex.Message}");
            }
        }

        var primary = new Cover
        {
            CoverLetter = e.CoverLetter,
            // A real UPC when the source gives one (comicade.com, or now
            // LOCG's own detail page via EnrichIssueDetails above) - the
            // same generated placeholder as every other source
            // otherwise. The store sets the real barcode once the book
            // physically arrives either way (Inventory's cover-detail
            // grid), so this only matters until then.
            Barcode = !string.IsNullOrWhiteSpace(e.Upc) ? e.Upc : $"{barcodePrefix}-{Guid.NewGuid():N}",
            Price = e.Price ?? 0,
            StockQuantity = 0,
            CoverImagePath = coverImagePath,
            // LOCG's own raw variant text when this entry IS a variant
            // ("Cover B Ryan Sook", "Ryan Sook Variant") - useful on its
            // own (the New Books/Covers grid otherwise shows nothing but
            // a bare letter for one of these) and also what
            // AddMissingCoverVariants matches a guessed variant against
            // on a later re-scrape, since it has no UPC to match by.
            CoverArtist = e.VariantLabel,
            // comicade.com's solicit blurb, or now Metron's own issue
            // description (EnrichIssueDetails above) - this is what the
            // New Books detail popup shows as the issue's description.
            // Null for every other source.
            Description = string.IsNullOrWhiteSpace(e.Description) ? null : e.Description
        };

        var results = new List<(Cover, bool)> { (primary, e.CoverLetterIsGuess) };

        foreach (var v in e.Variants)
        {
            // Nothing to show for a variant with no cover image at all -
            // not worth a blank Cover row.
            if (string.IsNullOrWhiteSpace(v.ImageUrl)) continue;

            string? variantImagePath = null;
            try
            {
                var hint = $"{seriesName}_{issueNumber:0.##}_variant_{Guid.NewGuid():N}".Replace(" ", "_");
                variantImagePath = _comicVine.DownloadCoverImage(v.ImageUrl!, hint);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{seriesName} #{issueNumber:0.##} (variant \"{v.Name}\"): cover image download failed: {ex.Message}");
            }

            var guessedLetter = CoverLetterParser.GuessFromName(v.Name);
            var variantCover = new Cover
            {
                // A provisional value only - ResolveCoverLetters below
                // overwrites this for every guessed (IsGuess=true)
                // candidate once the whole batch is known, exactly like
                // the primary cover's own CoverLetter above.
                CoverLetter = guessedLetter ?? "A",
                Barcode = !string.IsNullOrWhiteSpace(v.Upc) ? v.Upc! : $"{barcodePrefix}-{Guid.NewGuid():N}",
                // A variant's own price when Metron has one (a foil/
                // virgin variant is almost always priced higher) -
                // falling back to the primary issue's price, then 0,
                // same as every other source's own missing-price
                // fallback.
                Price = v.Price ?? e.Price ?? 0,
                StockQuantity = 0,
                CoverImagePath = variantImagePath,
                CoverArtist = v.Name,
                Description = string.IsNullOrWhiteSpace(e.Description) ? null : e.Description
            };
            results.Add((variantCover, guessedLetter == null));
        }

        return results;
    }

    // Assigns final, non-colliding cover letters across a batch of
    // candidate covers that may share provisional/guessed letters (see
    // BuildCoversFromEntry) - confirmed live this was the actual gap
    // behind two different-looking symptoms: Ultimates #19 showing
    // three/four LOCG variants all labeled Cover B (nothing deduped a
    // batch of same-issue GUESSES against each other), and Action Comics
    // #1104 only ever getting Cover A from Metron even once variant
    // expansion existed (nothing assigned the newly-expanded variants
    // their own letters at all). A confidently-read letter always claims
    // its own slot first; a guess (or a confident letter that collides
    // with one already claimed - two sources both confidently saying
    // "Cover B" for the same issue, say) only ever fills whatever's left
    // over, A through Z and then V2, V3... beyond that (an issue with
    // more than 26 covers is not realistic, but this still has to not
    // crash/overwrite if it somehow happens).
    private static void ResolveCoverLetters(List<(Cover Cover, bool IsGuess)> candidates, IEnumerable<string> alreadyUsedLetters)
    {
        var used = new HashSet<string>(alreadyUsedLetters, StringComparer.OrdinalIgnoreCase);
        var pending = new List<Cover>();

        foreach (var (cover, isGuess) in candidates)
        {
            var letter = cover.CoverLetter?.Trim();
            if (!isGuess && !string.IsNullOrEmpty(letter) && used.Add(letter))
                continue; // confidently-known letter, slot claimed
            pending.Add(cover); // a guess, or a confident letter that collided
        }

        foreach (var cover in pending)
        {
            var next = NextFreeLetter(used);
            cover.CoverLetter = next;
            used.Add(next);
        }
    }

    private static string NextFreeLetter(HashSet<string> used)
    {
        for (var c = 'A'; c <= 'Z'; c++)
        {
            var s = c.ToString();
            if (!used.Contains(s)) return s;
        }
        for (var n = 2; ; n++)
        {
            var s = $"V{n}";
            if (!used.Contains(s)) return s;
        }
    }

    // An issue we already carry can still be missing cover variants that
    // a later week's scrape now knows about - a retailer-exclusive or
    // incentive cover in particular is often announced well after the
    // issue's initial solicit. This adds whatever covers aren't already
    // on the existing issue instead of the old behavior of skipping the
    // whole group just because the issue number itself was already
    // known. A confidently-known letter (or a real UPC, when the source
    // has one) is how a cover already on file is recognized and not
    // re-added; a GUESSED variant with no UPC at all (LOCG essentially
    // never has one) has nothing that reliable to match against, so it
    // falls back to matching by CoverArtist (the raw variant text/name -
    // see BuildCoversFromEntry) - not perfect (a variant whose text
    // changes slightly between scrapes could still re-add), but far
    // better than the old behavior of every guess colliding on a single
    // hardcoded "B" and never growing past one slot at all.
    private void AddMissingCoverVariants(Issue existingIssue, IEnumerable<ComicListEntry> group, string seriesName, string barcodePrefix, AutoFetchResult result)
    {
        var existingLetters = existingIssue.Covers
            .Select(c => c.CoverLetter)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingBarcodes = existingIssue.Covers
            .Select(c => c.Barcode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingArtists = existingIssue.Covers
            .Where(c => !string.IsNullOrWhiteSpace(c.CoverArtist))
            .Select(c => c.CoverArtist!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<(Cover Cover, bool IsGuess)>();
        foreach (var e in group)
        {
            foreach (var (cover, isGuess) in BuildCoversFromEntry(e, seriesName, existingIssue.IssueNumber, barcodePrefix, result))
            {
                if (existingBarcodes.Contains(cover.Barcode)) continue;
                if (!isGuess && existingLetters.Contains(cover.CoverLetter)) continue;
                if (isGuess && !string.IsNullOrWhiteSpace(cover.CoverArtist) && existingArtists.Contains(cover.CoverArtist)) continue;
                candidates.Add((cover, isGuess));
            }
        }

        if (candidates.Count == 0) return;

        ResolveCoverLetters(candidates, existingLetters);
        foreach (var (cover, _) in candidates)
            existingIssue.Covers.Add(cover);

        result.Added.Add($"{seriesName} #{existingIssue.IssueNumber:0.##}: +{candidates.Count} new cover variant(s) found on a later scrape.");
    }

    private void DiscoverNewNumberOnes(List<Series> allSeries, AutoFetchResult result)
    {
        var trackedPublishers = (_storeSettings.Load().TrackedPublishers ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var knownVolumeIds = allSeries.Where(s => s.ComicVineVolumeId != null).Select(s => s.ComicVineVolumeId!.Value).ToHashSet();
        var knownNames = allSeries.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (trackedPublishers.Count > 0)
            DiscoverByTrackedPublishers(trackedPublishers, knownVolumeIds, knownNames, result);
        else
            DiscoverGlobalNumberOnes(knownVolumeIds, knownNames, result);
    }

    private void DiscoverByTrackedPublishers(List<string> trackedPublishers, HashSet<int> knownVolumeIds, HashSet<string> knownNames, AutoFetchResult result)
    {
        var (since, until) = DiscoveryWindow();

        // Most-recently-updated volumes first (see GetActiveVolumesByPublisher) -
        // a real ongoing flagship title updates constantly and sorts near
        // the top, right alongside brand-new launches. Each volume costs a
        // throttled, rate-limit-respecting HTTP call (see ComicVineService),
        // so this is kept modest - 30 per publisher is enough to cover a
        // publisher's genuinely active ongoing titles without a multi-
        // publisher run taking too long.
        const int volumesPerPublisher = 30;

        foreach (var publisher in trackedPublishers)
        {
            List<ComicVineVolumeMatch> volumes;
            try
            {
                volumes = _comicVine.GetActiveVolumesByPublisher(publisher, volumesPerPublisher);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{publisher}: {ex.Message}");
                // Every other tracked publisher would hit the exact same
                // wall right now - stop instead of burning through the
                // rest of the list one losing call at a time.
                if (IsRateLimitError(ex)) break;
                continue;
            }

            foreach (var vol in volumes)
            {
                if (knownVolumeIds.Contains(vol.Id)) continue; // already tracked - the per-series loop already covers it
                if (knownNames.Contains(vol.Name)) continue; // likely already in inventory, just not linked yet - don't create a duplicate

                List<ComicVineNewIssue> cvIssues;
                try
                {
                    cvIssues = _comicVine.GetIssuesInRange(vol.Id, since, until);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{vol.Name}: {ex.Message}");
                    // Same reasoning as the publisher-level catch above -
                    // a rate limit here means every remaining volume for
                    // every remaining publisher would fail the same way.
                    if (IsRateLimitError(ex)) return;
                    continue;
                }
                if (cvIssues.Count == 0) continue; // nothing from this volume ships in the window - skip it, don't create an empty Series

                var series = new Series
                {
                    Name = vol.Name,
                    StartYear = vol.StartYear ?? DateTime.Today.Year,
                    ComicVineVolumeId = vol.Id,
                    LastCheckedForNewIssues = DateTime.Now
                };
                _db.AllSeries.Add(series);
                knownVolumeIds.Add(vol.Id);
                knownNames.Add(series.Name);

                var knownNumbers = new HashSet<float>();
                foreach (var cvIssue in cvIssues)
                    AddIssueIfNew(series, cvIssue, knownNumbers, result);
            }
        }
    }

    // No Tracked Publishers set - casts the widest net (every #1 on Comic
    // Vine, any publisher) but each candidate costs a blocking HTTP call to
    // find its publisher, so this is capped to keep a single check fast;
    // anything past the cap is just picked up on the next check.
    private void DiscoverGlobalNumberOnes(HashSet<int> knownVolumeIds, HashSet<string> knownNames, AutoFetchResult result)
    {
        var (since, until) = DiscoveryWindow();

        List<ComicVineNewIssue> candidates;
        try
        {
            candidates = _comicVine.GetNewNumberOneIssues(since, until);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"New-#1 discovery: {ex.Message}");
            return;
        }

        // A volume can list more than one "#1" (reprints, one-shots under
        // the same volume, etc.) - only act on each volume once per run.
        var seenVolumeIds = new HashSet<int>();

        const int maxVolumeLookupsPerRun = 40;
        var volumeLookupsDone = 0;

        foreach (var cv in candidates)
        {
            if (cv.VolumeId == null) continue; // can't create/link a Series without a volume to point at
            var volumeId = cv.VolumeId.Value;

            if (!seenVolumeIds.Add(volumeId)) continue;
            if (knownVolumeIds.Contains(volumeId)) continue; // already tracked - the per-series loop already covers it
            if (knownNames.Contains(cv.VolumeName)) continue; // likely the same series already in inventory, just not linked yet - don't create a duplicate

            if (volumeLookupsDone >= maxVolumeLookupsPerRun)
            {
                result.Errors.Add($"Stopped new-#1 discovery early after {maxVolumeLookupsPerRun} candidates to keep this check fast - set Tracked Publishers in Settings for a faster, more relevant search instead.");
                break;
            }
            volumeLookupsDone++;

            ComicVineVolumeDetails? details;
            try
            {
                details = _comicVine.GetVolumeDetails(volumeId);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{cv.VolumeName}: {ex.Message}");
                // Every remaining candidate would hit the same rate limit
                // right now - stop instead of grinding through the rest.
                if (IsRateLimitError(ex)) break;
                continue;
            }

            if (details == null) continue;

            var series = new Series
            {
                Name = string.IsNullOrWhiteSpace(details.Name) ? cv.VolumeName : details.Name,
                StartYear = details.StartYear ?? DateTime.Today.Year,
                ComicVineVolumeId = volumeId,
                LastCheckedForNewIssues = DateTime.Now
            };

            _db.AllSeries.Add(series);
            knownNames.Add(series.Name);

            AddIssueIfNew(series, cv, new HashSet<float>(), result);
        }
    }

    public List<Issue> GetNewBooks()
    {
        var since = DateOnly.FromDateTime(DateTime.Today.AddMonths(-3));
        var until = DateOnly.FromDateTime(DateTime.Today.AddMonths(3));

        return _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .Where(i =>
                (i.ReleaseDate != null && i.ReleaseDate >= since && i.ReleaseDate <= until) ||
                (i.FocDate != null && i.FocDate >= since && i.FocDate <= until))
            .ToList();
    }
}
