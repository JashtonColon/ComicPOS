using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using ComicPos.Core;
using ComicPos.Core.Models;
using ComicPos.Core.Services;
using ComicPos.Importers;
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
    private readonly ICustomerService _customers;

    public AutoFetchService(ComicDbContext db, IComicVineService comicVine, IComicListService comicList, IComicadeFocService comicadeFoc, IGcdService gcd, IMetronService metron, ILocgService locg, IStoreSettingsService storeSettings, ICustomerService customers)
    {
        _db = db;
        _comicVine = comicVine;
        _comicList = comicList;
        _comicadeFoc = comicadeFoc;
        _gcd = gcd;
        _metron = metron;
        _locg = locg;
        _storeSettings = storeSettings;
        _customers = customers;
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
            trace.AppendLine();
            trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
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

        var focDatesFixed = FixImplausibleFocDates();
        if (focDatesFixed > 0)
            trace.AppendLine($"Found {focDatesFixed} issue(s) with an impossible FOC date (before year 2000, from a parser bug now fixed) - corrected to an estimate based on their release date.");

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
        // Two new-this-round counters (see the auto-link branch
        // below) - PRH's own feed never includes a cover image or a
        // Comic Vine id, so a brand-new PRH-sourced series starts
        // completely unlinked, and before this fix only Metron/GCD
        // (below) ever got a shot at its missing covers.
        var newlyLinkedThisRescan = 0;
        var skippedAutoLinkCooldown = 0;
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

            if (!comicVineRateLimited)
            {
                try
                {
                    if (series.ComicVineVolumeId != null)
                    {
                        ReverifyVolumeLink(series, result);
                        BackfillFromComicVine(series, result);
                    }
                    // A never-linked series never got a link ATTEMPT here
                    // before this fix - only Metron/GCD ran for it below,
                    // and Metron's IP-blocked while GCD caps out around 30
                    // requests/hour. The PRH automation (see
                    // metron-integration-notes.md) made this the dominant
                    // case overnight: thousands of brand-new PRH-sourced
                    // series/issues with zero cover art and zero Comic Vine
                    // link, since PRH's own feed never includes a cover
                    // image or a Comic Vine id. TryAutoLink already applies
                    // the "never guess" exact-match-or-nothing rule (plus
                    // its own collected-edition fallback, which matters a
                    // lot given how omnibus/TP-heavy PRH's catalog is) and
                    // stamps LastAutoLinkAttempt itself - the cooldown
                    // check here just avoids re-asking Comic Vine about a
                    // title it already said no to within the last 24h.
                    else if (series.LastAutoLinkAttempt == null ||
                             DateTime.Now - series.LastAutoLinkAttempt.Value >= AutoLinkRetryCooldown)
                    {
                        if (TryAutoLink(series, result))
                        {
                            newlyLinkedThisRescan++;
                            BackfillFromComicVine(series, result);
                        }
                    }
                    else
                    {
                        skippedAutoLinkCooldown++;
                    }
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

        trace.AppendLine($"Comic Vine auto-link (previously-unlinked series): {newlyLinkedThisRescan} linked and backfilled this run" + (skippedAutoLinkCooldown > 0 ? $" ({skippedAutoLinkCooldown} skipped - auto-link tried within the last {AutoLinkRetryCooldown.TotalHours:0}h and didn't match)." : "."));
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
        trace.AppendLine();
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
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
            trace.AppendLine();
            trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
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
    // Shared by every long-running method below that calls SaveChanges()
    // once per week/chunk/file in a loop against this SAME _db instance
    // (RunBackfillYearFromLocg, RunCheckForNewIssuesViaMetron,
    // ImportLunarDistributionRelease/WalkAndImportLunarDates) - added
    // 2026-11-24 after a real "Backfill All Lunar History" run showed
    // EVERY SINGLE week after the first real failure also failing, all
    // with the exact same generic "An error occurred while saving the
    // entity changes. See the inner exception for details." message
    // repeated upwards of 80 times in a row (109 "weeks imported" were
    // logged by the walk, but only the first 5 actually saved anything -
    // see WalkAndImportLunarDates' own counter fix, same bug, same root
    // cause).
    //
    // Root cause: EF Core does NOT automatically revert entities to
    // their pre-change state when SaveChanges() throws - a half-applied
    // batch of Added/Modified entities stays tracked exactly as it was -
    // so the VERY NEXT SaveChanges() call on the SAME DbContext tries
    // (and fails) to save those same bad entities all over again, on top
    // of whatever new data that next iteration added, compounding
    // forever. A single bad week/chunk/file was silently taking down
    // every later one in the same run. The three loops above all reuse
    // one shared _db for their whole (often hours-long) run specifically
    // so earlier iterations' work gets flushed to disk incrementally
    // (see each one's own "persist incrementally" comment) - exactly the
    // design that makes this failure mode possible if a save ever does
    // fail partway through.
    //
    // Fix: clear the change tracker immediately after a failed save, so
    // the next iteration starts clean regardless of what the failed one
    // left behind. Also walks the real InnerException chain for the
    // actual detail (DbUpdateException's own Message is always the same
    // unhelpful boilerplate - the real SQLite/EF reason, e.g. which
    // unique index was violated, is one or more levels down), so a
    // future failure like this one is diagnosable from the trace log
    // instead of needing to be guessed at the way this one had to be.
    private (bool Success, string? Error) TrySaveChanges()
    {
        try
        {
            _db.SaveChanges();
            return (true, null);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
                parts.Add(e.Message);
            return (false, string.Join(" -> ", parts));
        }
    }

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
                // persist incrementally - a later week's failure shouldn't lose earlier weeks' work
                var (saved, saveError) = TrySaveChanges();
                progressLine = saved
                    ? $"Week {i + 1}/{mondays.Count} ({week:MM/dd/yyyy}): {entries.Count} entries from LOCG, +{added} new issue(s)."
                    : $"Week {i + 1}/{mondays.Count} ({week:MM/dd/yyyy}): {entries.Count} entries matched but FAILED TO SAVE - {saveError}";
                trace.AppendLine(progressLine);
                if (!saved) result.Errors.Add($"LOCG week of {week:MM/dd/yyyy}: failed to save - {saveError}");
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
            trace.AppendLine();
            trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
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
                // persist incrementally - a later chunk's failure shouldn't lose earlier chunks' work
                var (saved, saveError) = TrySaveChanges();
                progressLine = saved
                    ? $"Week {i + 1}/{chunks.Count} ({start:MM/dd/yyyy}-{end:MM/dd/yyyy}): {entries.Count} issue(s) from Metron, +{added} new issue(s)."
                    : $"Week {i + 1}/{chunks.Count} ({start:MM/dd/yyyy}-{end:MM/dd/yyyy}): {entries.Count} issue(s) matched but FAILED TO SAVE - {saveError}";
                trace.AppendLine(progressLine);
                if (!saved) result.Errors.Add($"Metron {start:MM/dd/yyyy}-{end:MM/dd/yyyy}: failed to save - {saveError}");
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
    public AutoFetchResult RetryMetronEnrichment(Action<string>? onProgress = null, int? limit = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Retry Metron enrichment run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + (limit.HasValue ? $" (TEST RUN - capped at {limit} issue(s))" : ""));
        trace.AppendLine(new string('=', 60));

        var candidates = _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .ToList()
            .Where(i => i.Covers.Any(c => c.Price == 0 && c.Barcode.StartsWith("MTR-")))
            // Closest to TODAY first, not whatever order SQLite's table
            // scan happens to return (effectively insertion order) and not
            // a plain OrderByDescending either - added 2026-11-23 after two
            // failed attempts at ordering the LOCG counterpart below:
            // unordered picked 5 of the OLDEST unenriched rows, which
            // turned out to be old solicitation-catalog/digital-only junk
            // (Marvel Previews, an IDW title catalog, several "Infinity
            // Comic" digital exclusives); plain newest-first then picked
            // issues solicited as far out as March 2027 - 15 months away -
            // which LOCG's own site couldn't reliably serve (HTTP 403,
            // self-interrupted navigations), presumably because its new-
            // releases calendar doesn't really have usable data that far
            // ahead. Sorting by DISTANCE from today instead (not direction)
            // means a `limit` test run samples whatever's actually close to
            // "now" either way - real, current single issues, the same
            // kind GetNewBooks' 3-month window shows.
            .OrderBy(i => i.ReleaseDate.HasValue
                ? Math.Abs((i.ReleaseDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.Now).TotalDays)
                : double.MaxValue)
            .ToList();
        if (limit.HasValue) candidates = candidates.Take(limit.Value).ToList();

        onProgress?.Invoke($"Found {candidates.Count} Metron-sourced issue(s) still showing placeholder price/barcode.");

        // Shared across every issue in this run - see Bug #10's fix on
        // AddMissingCoverVariants/AddEntriesAsIssues' batchClaimedBarcodes
        // for why a per-issue set isn't enough.
        // Bug #12 fix (2026-11-24): seeded from the WHOLE Covers table, not
        // left empty - see AddEntriesAsIssues' own batchClaimedBarcodes doc
        // comment for the full story (a cover here can collide with one
        // created by a totally different earlier run, e.g. a facsimile
        // reprint whose barcode got merged onto the wrong issue before the
        // facsimile-title fix existed).
        var batchClaimedBarcodes = _db.Covers.Select(c => c.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var issue in candidates)
        {
            done++;
            var seriesName = issue.Series.Name;
            // Set on every path below (success or failure) before this
            // iteration's trace line gets written - added 2026-11-23
            // after a run where every one of 5 test issues failed on the
            // early "couldn't re-find" continue, which skipped the old
            // once-every-5 checkpoint entirely, leaving the debug file
            // showing nothing but a start/finish timestamp with zero
            // detail on what actually happened in between.
            string outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: (no outcome recorded - this is a bug if you see it)";
            try
            {
                if (!issue.MetronIssueId.HasValue)
                {
                    // Added before Issue.MetronIssueId was persisted at
                    // all (the exact outage window this method exists
                    // for) - re-find it by the same series_name+number
                    // search Check Now originally used to match this
                    // issue against Metron in the first place.
                    var (foundId, failureReason) = _metron.FindIssueId(seriesName, issue.IssueNumber, issue.ReleaseDate);
                    if (!foundId.HasValue)
                    {
                        outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: couldn't re-find this issue on Metron - {failureReason}";
                        result.Errors.Add(outcomeLine);
                        trace.AppendLine(outcomeLine);
                        onProgress?.Invoke(outcomeLine);
                        DebugLog.WriteCheckNowLog(trace.ToString());
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
                AddMissingCoverVariants(issue, new List<ComicListEntry> { tempEntry }, seriesName, "MTR", result, batchClaimedBarcodes);

                outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: re-enriched from Metron ({variantsFound} variant(s) on file there).";
                result.Added.Add(outcomeLine);
            }
            catch (Exception ex)
            {
                outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: retry enrichment failed: {ex.Message}";
                result.Errors.Add(outcomeLine);
            }

            // Every issue's outcome goes to the trace/disk immediately,
            // not batched every 5 like the old code - the network call
            // above already takes 3.5s+ (more on a retry), so one extra
            // file write per issue is free by comparison, and it's what
            // actually makes the debug file useful instead of silent
            // until a batch boundary that a run of all-failures never
            // reaches. SaveChanges stays batched - no reason to hit the
            // DB that often.
            trace.AppendLine(outcomeLine);
            onProgress?.Invoke(outcomeLine);
            if (done % 5 == 0 || done == candidates.Count) _db.SaveChanges();
            DebugLog.WriteCheckNowLog(trace.ToString());
        }

        _db.SaveChanges();
        if (done == 0) trace.AppendLine("No issues needed retrying.");
        trace.AppendLine();
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
        result.Trace = trace.ToString();
        // Always write once more here, even if the loop above never ran
        // (zero candidates) or never hit a done%5==0/last-item checkpoint -
        // otherwise a trivial run (nothing to retry) would leave the debug
        // file showing a stale trace from whatever ran before it.
        DebugLog.WriteCheckNowLog(result.Trace);
        return result;
    }

    // LOCG counterpart to RetryMetronEnrichment above - same job (go back
    // and fill in Publisher/UPC/price/description/covers for an issue
    // that's already on file but still showing a generated placeholder),
    // sourced from LOCG's weekly list/detail pages instead of Metron's
    // API. Added 2026-11-23 specifically because Metron itself is
    // IP/network-blocked right now (see metron-integration-notes.md) -
    // this is the one enrichment path that doesn't depend on Metron being
    // reachable at all, so the same backfill can still make progress
    // while that's sorted out. Matches BOTH "MTR-" and "LOCG-" placeholder
    // covers (not just Metron's), since a LOCG-added issue can end up
    // with the same stuck placeholder if its own detail-page enrichment
    // failed at creation time (BuildCoversFromEntry's try/catch around
    // ILocgService.EnrichIssueDetails just logs and moves on) - this is a
    // general "go check LOCG again" pass, not a narrowly Metron-only one.
    //
    // There's no stored LOCG-specific id on Issue the way Issue.
    // MetronIssueId exists for Metron (this path didn't exist until now),
    // so matching against LOCG's weekly list has to go by series name
    // (SeriesNameKey.Normalize - the same key every other matcher in this
    // file already uses) plus issue number instead of a direct id lookup.
    //
    // A release week is fetched from LOCG (_locg.GetWeekEntries) at most
    // ONCE per distinct Monday across the whole run - cached in weekCache
    // below, success or failure. LOCG's own AJAX endpoint only lets a
    // request through roughly once every 4-5 minutes (see ILocgService),
    // so re-fetching the same week once per issue instead of once per
    // week would turn a batch of same-week candidates into a run taking
    // hours longer than it needs to, and risks exhausting LocgService's
    // own retry budget for nothing. A week that throws is cached as a
    // failure too, so it isn't silently retried for every other candidate
    // that happens to ship that same week.
    public AutoFetchResult RetryLocgEnrichment(Action<string>? onProgress = null, int? limit = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Retry LOCG enrichment run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + (limit.HasValue ? $" (TEST RUN - capped at {limit} issue(s))" : ""));
        trace.AppendLine(new string('=', 60));

        var candidates = _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .ToList()
            .Where(i => i.Covers.Any(c => c.Price == 0 && (c.Barcode.StartsWith("MTR-") || c.Barcode.StartsWith("LOCG-"))))
            // Closest to today first - see the identical comment on
            // RetryMetronEnrichment's candidate query above. Confirmed
            // live 2026-11-23, twice: unordered picked 5 old solicitation-
            // catalog/digital-only entries (Marvel Previews, an IDW title
            // catalog, several "Infinity Comic" exclusives); plain
            // newest-first then picked issues solicited as far out as
            // March 2027, which LOCG's own site 403'd or self-interrupted
            // navigating to (its new-releases calendar doesn't seem to
            // have usable data that far ahead). Distance-from-today avoids
            // both extremes.
            .OrderBy(i => i.ReleaseDate.HasValue
                ? Math.Abs((i.ReleaseDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.Now).TotalDays)
                : double.MaxValue)
            .ToList();
        if (limit.HasValue) candidates = candidates.Take(limit.Value).ToList();

        onProgress?.Invoke($"Found {candidates.Count} issue(s) still showing placeholder price/barcode to try against LOCG.");

        var weekCache = new Dictionary<DateOnly, (List<ComicListEntry>? Entries, string? Error)>();

        // Shared across every issue in this run - see Bug #10's fix on
        // AddMissingCoverVariants/AddEntriesAsIssues' batchClaimedBarcodes
        // for why a per-issue set isn't enough.
        // Bug #12 fix (2026-11-24): seeded from the WHOLE Covers table, not
        // left empty - see AddEntriesAsIssues' own batchClaimedBarcodes doc
        // comment for the full story (a cover here can collide with one
        // created by a totally different earlier run, e.g. a facsimile
        // reprint whose barcode got merged onto the wrong issue before the
        // facsimile-title fix existed).
        var batchClaimedBarcodes = _db.Covers.Select(c => c.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        foreach (var issue in candidates)
        {
            done++;
            var seriesName = issue.Series.Name;
            // Same real-time-write pattern as RetryMetronEnrichment above -
            // set on every path (success or failure) before this
            // iteration's trace line gets written, so an all-failures run
            // still leaves a readable debug file instead of a blank one.
            string outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: (no outcome recorded - this is a bug if you see it)";
            try
            {
                if (!issue.ReleaseDate.HasValue)
                {
                    outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: skipped - no release date on file, can't tell which LOCG week to check.";
                    result.Errors.Add(outcomeLine);
                    trace.AppendLine(outcomeLine);
                    onProgress?.Invoke(outcomeLine);
                    DebugLog.WriteCheckNowLog(trace.ToString());
                    continue;
                }

                var releaseDate = issue.ReleaseDate.Value;
                // Same Monday-of-week formula RunBackfillYearFromLocg uses
                // (DayOfWeek: Sunday=0...Saturday=6, Monday=1) - LOCG's
                // weekly pages are always Monday-to-Monday.
                var monday = releaseDate.AddDays(-(((int)releaseDate.DayOfWeek + 6) % 7));

                if (!weekCache.TryGetValue(monday, out var week))
                {
                    try
                    {
                        var entries = _locg.GetWeekEntries(monday);
                        week = (entries, null);
                    }
                    catch (Exception ex)
                    {
                        week = (null, ex.Message);
                    }
                    weekCache[monday] = week;
                }

                if (week.Entries == null)
                {
                    outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: couldn't fetch LOCG's {monday:MM/dd/yyyy} week - {week.Error}";
                    result.Errors.Add(outcomeLine);
                    trace.AppendLine(outcomeLine);
                    onProgress?.Invoke(outcomeLine);
                    DebugLog.WriteCheckNowLog(trace.ToString());
                    continue;
                }

                var seriesKey = SeriesNameKey.Normalize(seriesName);
                var matches = week.Entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.SeriesName)
                        && SeriesNameKey.Normalize(e.SeriesName!) == seriesKey
                        && e.IssueNumber.HasValue
                        && Math.Abs(e.IssueNumber.Value - issue.IssueNumber) < 0.01f)
                    .ToList();

                if (matches.Count == 0)
                {
                    outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: not found in LOCG's {monday:MM/dd/yyyy} week ({week.Entries.Count} entries that week).";
                    result.Errors.Add(outcomeLine);
                    trace.AppendLine(outcomeLine);
                    onProgress?.Invoke(outcomeLine);
                    DebugLog.WriteCheckNowLog(trace.ToString());
                    continue;
                }

                // The Cover A entry when LOCG has one, otherwise just
                // whichever matched first - this is the one whose detail
                // page gets fetched for a real UPC (the weekly LIST page
                // never carries one - see ILocgService.EnrichIssueDetails)
                // and whose Publisher/Price/Description backfill the
                // issue/stale-cover's own fields below.
                var primary = matches.FirstOrDefault(e => string.Equals(e.CoverLetter, "A", StringComparison.OrdinalIgnoreCase)) ?? matches[0];

                if (!string.IsNullOrWhiteSpace(primary.DetailUrl))
                {
                    try { _locg.EnrichIssueDetails(primary); }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{seriesName} #{issue.IssueNumber:0.##}: LOCG detail page enrichment failed: {ex.Message}");
                    }
                }

                var staleCover = issue.Covers.FirstOrDefault(c => c.Price == 0 && (c.Barcode.StartsWith("MTR-") || c.Barcode.StartsWith("LOCG-")));
                if (staleCover != null)
                {
                    if (!string.IsNullOrWhiteSpace(primary.Upc)) staleCover.Barcode = primary.Upc!;
                    if (primary.Price.HasValue) staleCover.Price = primary.Price.Value;
                    if (!string.IsNullOrWhiteSpace(primary.Description)) staleCover.Description = primary.Description;
                }
                if (!string.IsNullOrWhiteSpace(primary.Publisher) && string.IsNullOrWhiteSpace(issue.Publisher))
                    issue.Publisher = primary.Publisher;

                // Clear DetailUrl now that primary's own detail page has
                // already been fetched above - same reason
                // RetryMetronEnrichment clears tempEntry.MetronIssueId
                // before its own AddMissingCoverVariants call: without
                // this, BuildCoversFromEntry (called inside
                // AddMissingCoverVariants) would pay for a second,
                // redundant ~30s detail-page fetch for data already in
                // hand. Any OTHER entry in matches (a variant LOCG listed
                // that isn't on file yet) keeps its own DetailUrl, so its
                // own detail page still gets fetched the first time it's
                // actually turned into a new Cover row - exactly like a
                // brand-new issue already works.
                primary.DetailUrl = null;
                AddMissingCoverVariants(issue, matches, seriesName, "LOCG", result, batchClaimedBarcodes);

                outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: re-enriched from LOCG ({matches.Count} matching entr{(matches.Count == 1 ? "y" : "ies")} that week).";
                result.Added.Add(outcomeLine);
            }
            catch (Exception ex)
            {
                outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: retry enrichment failed: {ex.Message}";
                result.Errors.Add(outcomeLine);
            }

            trace.AppendLine(outcomeLine);
            onProgress?.Invoke(outcomeLine);
            if (done % 5 == 0 || done == candidates.Count) _db.SaveChanges();
            DebugLog.WriteCheckNowLog(trace.ToString());
        }

        _db.SaveChanges();
        if (done == 0) trace.AppendLine("No issues needed retrying.");
        trace.AppendLine();
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
        result.Trace = trace.ToString();
        // Always write once more here, even if the loop above never ran
        // (zero candidates) or never hit a done%5==0/last-item checkpoint -
        // otherwise a trivial run (nothing to retry) would leave the debug
        // file showing a stale trace from whatever ran before it.
        DebugLog.WriteCheckNowLog(result.Trace);
        return result;
    }

    // Hand-picked verification target, added 2026-11-24 - NOT LOCG (see
    // RetryLocgEnrichment above), deliberately one of the no-Cloudflare
    // sources instead: comicade.com's FOC catalogue, the same one
    // "Check Now (no Cloudflare)" already uses. Built after both the
    // LOCG candidate query AND a manual LOCG click stopped working the
    // same evening (Cloudflare rejecting even a real human click - see
    // metron-integration-notes.md), so this checks two SPECIFIC,
    // already-known issues against a source that has no Cloudflare step
    // to get stuck on at all - comicade.com's Inertia JSON endpoint is a
    // plain HTTP GET. Deliberately hardcoded, not a general "enrich any
    // issue by name" feature - a one-off diagnostic button.
    //
    // Important limitation, inherited from IComicadeFocService.
    // GetUpcomingFocEntries: comicade.com's own catalogue is FORWARD-
    // looking only (today through roughly 2 months out) - there's no way
    // to ask it for an already-past week the way LOCG's dated URL allows.
    // If either target issue's FOC/street date has already passed,
    // Comicade simply won't have it on file any more, and this reports
    // that plainly rather than silently finding nothing.
    //
    // Does NOT require the issue to currently show a placeholder price/
    // barcode (unlike RetryLocgEnrichment/RetryMetronEnrichment) - these
    // two may already have real data on file, and the point here is
    // purely to confirm the match/enrich logic produces the right answer
    // for issues the user can check against Comicade's own site by hand.
    public AutoFetchResult RetryComicadeEnrichmentForSpecificIssues(Action<string>? onProgress = null)
    {
        var targets = new (string SeriesName, float IssueNumber)[]
        {
            ("Absolute Wonder Woman", 26f),
            ("Absolute Flash", 21f)
        };

        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Retry Comicade FOC enrichment (specific issues) run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        var allIssues = _db.Issues.Include(i => i.Series).Include(i => i.Covers).ToList();
        var candidates = new List<Issue>();
        foreach (var (targetName, targetNumber) in targets)
        {
            var targetKey = SeriesNameKey.Normalize(targetName);
            var found = allIssues.FirstOrDefault(i =>
                SeriesNameKey.Normalize(i.Series.Name) == targetKey
                && Math.Abs(i.IssueNumber - targetNumber) < 0.01f);
            if (found != null)
            {
                candidates.Add(found);
            }
            else
            {
                var line = $"{targetName} #{targetNumber:0.##}: not found in inventory at all - can't retry something that was never added.";
                result.Errors.Add(line);
                trace.AppendLine(line);
            }
        }

        onProgress?.Invoke($"Found {candidates.Count}/{targets.Length} target issue(s) in inventory - checking comicade.com's FOC catalogue...");

        // Shared across every issue in this run - see Bug #10's fix on
        // AddMissingCoverVariants/AddEntriesAsIssues' batchClaimedBarcodes
        // for why a per-issue set isn't enough.
        // Bug #12 fix (2026-11-24): seeded from the WHOLE Covers table, not
        // left empty - see AddEntriesAsIssues' own batchClaimedBarcodes doc
        // comment for the full story (a cover here can collide with one
        // created by a totally different earlier run, e.g. a facsimile
        // reprint whose barcode got merged onto the wrong issue before the
        // facsimile-title fix existed).
        var batchClaimedBarcodes = _db.Covers.Select(c => c.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ComicListEntry> comicadeEntries;
        try
        {
            comicadeEntries = _comicadeFoc.GetUpcomingFocEntries();
        }
        catch (Exception ex)
        {
            var line = $"Comicade FOC catalogue unreachable: {ex.Message}";
            result.Errors.Add(line);
            trace.AppendLine(line);
            trace.AppendLine();
            trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            return result;
        }

        var done = 0;
        foreach (var issue in candidates)
        {
            done++;
            var seriesName = issue.Series.Name;
            string outcomeLine;
            try
            {
                var seriesKey = SeriesNameKey.Normalize(seriesName);
                var matches = comicadeEntries
                    .Where(e => !string.IsNullOrWhiteSpace(e.SeriesName)
                        && SeriesNameKey.Normalize(e.SeriesName!) == seriesKey
                        && e.IssueNumber.HasValue
                        && Math.Abs(e.IssueNumber.Value - issue.IssueNumber) < 0.01f)
                    .ToList();

                if (matches.Count == 0)
                {
                    outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: not in Comicade's current FOC window ({comicadeEntries.Count} entries total right now) - likely already past its FOC/street date, which this source doesn't keep historical data for.";
                    result.Errors.Add(outcomeLine);
                }
                else
                {
                    // Comicade entries already carry everything (Upc,
                    // Price, Description, CoverImageUrl) straight off its
                    // own JSON - no separate detail-page fetch needed the
                    // way LOCG/Metron require (see ComicadeFocService).
                    var primary = matches.FirstOrDefault(e => string.Equals(e.CoverLetter, "A", StringComparison.OrdinalIgnoreCase)) ?? matches[0];

                    var primaryCover = issue.Covers.FirstOrDefault(c => string.Equals(c.CoverLetter, "A", StringComparison.OrdinalIgnoreCase))
                        ?? issue.Covers.FirstOrDefault();
                    if (primaryCover != null)
                    {
                        if (!string.IsNullOrWhiteSpace(primary.Upc)) primaryCover.Barcode = primary.Upc!;
                        if (primary.Price.HasValue) primaryCover.Price = primary.Price.Value;
                        if (!string.IsNullOrWhiteSpace(primary.Description)) primaryCover.Description = primary.Description;
                    }
                    if (!string.IsNullOrWhiteSpace(primary.Publisher))
                        issue.Publisher = primary.Publisher;

                    AddMissingCoverVariants(issue, matches, seriesName, "CFC", result, batchClaimedBarcodes);

                    outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: re-enriched from Comicade ({matches.Count} matching entr{(matches.Count == 1 ? "y" : "ies")}) - Barcode={primaryCover?.Barcode}, Price={primaryCover?.Price}, Publisher={issue.Publisher}.";
                    result.Added.Add(outcomeLine);
                }
            }
            catch (Exception ex)
            {
                outcomeLine = $"{done}/{candidates.Count} {seriesName} #{issue.IssueNumber:0.##}: retry enrichment failed: {ex.Message}";
                result.Errors.Add(outcomeLine);
            }

            trace.AppendLine(outcomeLine);
            onProgress?.Invoke(outcomeLine);
        }

        _db.SaveChanges();
        trace.AppendLine();
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        return result;
    }

    // Imports a Lunar Distribution weekly release Excel file - added
    // 2026-11-24 after confirming (via direct Python/openpyxl inspection
    // of a real file) that it carries real UPC, price, publisher,
    // description, FOC date, street date, AND an embedded real cover
    // photo for every single product row, all with zero network/
    // Cloudflare/rate-limit risk since it's a file the store receives
    // directly from its own distributor rather than anything scraped.
    // See LunarDistributionParser/LunarTitleParser (ComicPos.Importers)
    // for how the file itself gets read and its titles parsed - this
    // method's own job is just the glue: save each row's embedded image
    // to disk (there's no URL to download, see ComicListEntry.
    // CoverImageLocalPath), turn each row into a ComicListEntry, and feed
    // the whole batch through the SAME AddEntriesAsIssues pipeline every
    // other source (ComicList/GCD/Metron/LOCG/Comicade) already uses -
    // not IImportService.ImportFromCsv's separate, older pipeline, which
    // has no cover-image handling at all. (Correction, 2026-11-24: an
    // earlier version of this comment claimed ImportFromCsv had "no UI
    // entry point" - that was wrong, it's Import.razor at /import, just a
    // separate CSV-only page from this one.)
    //
    // Called once per uploaded file (see NewBooks.razor's file picker) OR
    // once per FetchLunarReleaseFromWebsite call below, which downloads
    // the file itself instead of needing it uploaded by hand.
    public AutoFetchResult ImportLunarDistributionRelease(string filePath, Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Lunar Distribution import run at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({Path.GetFileName(filePath)})");
        trace.AppendLine(new string('=', 60));

        LunarParseResult parsed;
        try
        {
            parsed = LunarDistributionParser.Parse(filePath);
        }
        catch (Exception ex)
        {
            var line = $"Couldn't read the file: {ex.Message}";
            result.Errors.Add(line);
            trace.AppendLine(line);
            trace.AppendLine();
            trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            return result;
        }

        result.Errors.AddRange(parsed.Errors);
        foreach (var e in parsed.Errors) trace.AppendLine(e);

        onProgress?.Invoke($"Parsed {parsed.Rows.Count} comic row(s) from the file - saving cover images and matching against inventory...");

        var allSeries = _db.AllSeries.Include(s => s.Issues).ThenInclude(i => i.Covers).ToList();
        var entries = new List<ComicListEntry>();

        int imagesSaved = 0, imagesFailed = 0;
        foreach (var row in parsed.Rows)
        {
            string? localImagePath = null;
            if (row.ImageBytes is { Length: > 0 })
            {
                try
                {
                    var hint = $"{row.SeriesName}_{row.IssueNumber:0.##}_{row.CoverLetter}_{Guid.NewGuid():N}".Replace(" ", "_");
                    localImagePath = _comicVine.SaveCoverImageBytes(row.ImageBytes, hint, ".jpg");
                    imagesSaved++;
                }
                catch (Exception ex)
                {
                    imagesFailed++;
                    result.Errors.Add($"{row.SeriesName} #{row.IssueNumber:0.##}: couldn't save its embedded cover image: {ex.Message}");
                }
            }

            entries.Add(new ComicListEntry
            {
                Publisher = row.Publisher,
                RawTitle = row.RawTitle,
                SeriesName = row.SeriesName,
                IssueNumber = row.IssueNumber,
                CoverLetter = row.CoverLetter,
                Price = row.Price,
                ReleaseDate = row.ReleaseDate,
                FocDate = row.FocDate,
                Upc = row.Upc,
                Description = row.Description,
                CoverImageLocalPath = localImagePath,
                // A printing marker ("2ND Printing") is worth showing
                // over a bare artist name when both are somehow present -
                // see BuildCoversFromEntry's CoverArtist = e.VariantLabel.
                VariantLabel = row.PrintingLabel ?? row.VariantText,
                // Only set when Lunar's own title carried a "(YYYY)"
                // volume-year disambiguator (now stripped out of
                // SeriesName itself - see LunarTitleParser.
                // VolumeYearMarker) - lets AddEntriesAsIssues seed a
                // brand-new series with its real start year instead of
                // guessing from this week's release date, same as
                // Metron's own SeriesStartYearHint usage above.
                SeriesStartYearHint = row.SeriesStartYearHint
            });
        }

        trace.AppendLine($"{imagesSaved} cover image(s) saved to disk, {imagesFailed} failed.");
        onProgress?.Invoke($"{entries.Count} comic entr{(entries.Count == 1 ? "y" : "ies")} ready - matching against inventory...");

        AddEntriesAsIssues(allSeries, entries, DateOnly.FromDateTime(DateTime.Today), "LUN", result);

        // Was a bare _db.SaveChanges() - confirmed live 2026-11-24 that a
        // single file's failed save (e.g. a duplicate Cover.Barcode within
        // the batch - see AddEntriesAsIssues' cover-candidate de-dup fix
        // the same night) left its half-applied entities tracked on this
        // shared _db, so every LATER call to this same method from
        // WalkAndImportLunarDates' loop (one call per week during a full
        // backfill) failed the exact same way trying to re-save them -
        // see TrySaveChanges' own doc comment for the full story. Using
        // it here, same as the other two multi-iteration backfill loops,
        // means one bad file can no longer take an entire backfill run
        // down with it.
        var (saved, saveError) = TrySaveChanges();
        result.Saved = saved;
        if (!saved)
        {
            var line = $"Matched {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} but FAILED TO SAVE: {saveError}";
            result.Errors.Add(line);
            trace.AppendLine(line);
        }

        trace.AppendLine();
        if (result.Added.Count > 0)
        {
            trace.AppendLine("Added:");
            foreach (var line in result.Added) trace.AppendLine("  " + line);
            trace.AppendLine();
        }
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {(saved ? result.NewIssuesAdded : 0)} new issue(s) added.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke(saved
            ? $"Done - {result.NewIssuesAdded} new issue(s) added, {imagesSaved} cover image(s) saved."
            : $"Done - save failed, nothing from this file was added: {saveError}");
        return result;
    }

    // Imports PRH's "Comics Retail" solicitation spreadsheet - added
    // 2026-11-25, see IAutoFetchService's own doc comment for the full
    // rationale. Unlike ImportLunarDistributionRelease, this feed is
    // pure data (ISBN/UPC, Title, Creators, Imprint, price, Format, FOC/
    // On-Sale dates) with no embedded cover photo per row - a brand-new
    // series/issue from here starts with no cover at all, exactly like
    // any Metron/LOCG/GCD-sourced one that Comic Vine hasn't backfilled
    // yet, and gets picked up the same way by the existing auto-link +
    // cover-fetch pass (TryAutoLink / UpgradeLowResolutionCovers).
    //
    // PRH's own Format column (CB/TR/HC/NT/DG) is read straight onto
    // ComicListEntry.TypeHint rather than left for AddEntriesAsIssues'
    // usual name-pattern guess - see PrhCatalogRow.TypeHint's own doc
    // comment for why that guess alone isn't reliable enough for PRH's
    // naming conventions specifically.
    public AutoFetchResult ImportPrhCatalogRelease(string filePath, Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"PRH Comics Retail import run at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({Path.GetFileName(filePath)})");
        trace.AppendLine(new string('=', 60));

        PrhCatalogParseResult parsed;
        try
        {
            parsed = PrhCatalogParser.Parse(filePath);
        }
        catch (Exception ex)
        {
            var line = $"Couldn't read the file: {ex.Message}";
            result.Errors.Add(line);
            trace.AppendLine(line);
            trace.AppendLine();
            trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.");
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            return result;
        }

        result.Errors.AddRange(parsed.Errors);
        foreach (var e in parsed.Errors) trace.AppendLine(e);

        onProgress?.Invoke($"Parsed {parsed.Rows.Count} row(s) from the file - matching against inventory...");

        var allSeries = _db.AllSeries.Include(s => s.Issues).ThenInclude(i => i.Covers).ToList();
        var entries = parsed.Rows.Select(row => new ComicListEntry
        {
            Publisher = row.Publisher,
            RawTitle = row.RawTitle,
            SeriesName = row.SeriesName,
            IssueNumber = row.IssueNumber,
            CoverLetter = row.CoverLetter,
            Price = row.Price,
            ReleaseDate = row.ReleaseDate,
            FocDate = row.FocDate,
            Upc = row.Upc,
            Description = row.Creators,
            VariantLabel = row.VariantText,
            TypeHint = row.TypeHint
        }).ToList();

        onProgress?.Invoke($"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} ready - matching against inventory...");

        AddEntriesAsIssues(allSeries, entries, DateOnly.FromDateTime(DateTime.Today), "PRH", result);

        // Same per-file TrySaveChanges (rather than a bare SaveChanges)
        // as ImportLunarDistributionRelease - see that method's own doc
        // comment for why: one bad row's failed save shouldn't leave
        // half-applied entities tracked on this shared _db for whatever
        // gets imported next.
        var (saved, saveError) = TrySaveChanges();
        result.Saved = saved;
        if (!saved)
        {
            var line = $"Matched {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} but FAILED TO SAVE: {saveError}";
            result.Errors.Add(line);
            trace.AppendLine(line);
        }
        else
        {
            // Added 2026-11-26: used to be a wholly separate, manual
            // catch-up pass (BackfillCoverVariantsFromPrhBookPages,
            // still below, now kept as the whole-catalog version of
            // this same lookup) - the user pointed out the catalog
            // xlsx's own UPC cell is ITSELF a hyperlink straight to
            // this exact book's prhcomics.com page, and wanted that
            // followed as part of one linear import pipeline (xlsx ->
            // metadata -> covers/variants from the UPC lookup -> done),
            // not a separate click some time later. See
            // FetchAdditionalCoversForImportedEntries's own doc comment
            // for the full story, including why this needs no LOCG
            // involvement at all.
            onProgress?.Invoke($"{result.NewIssuesAdded} new issue(s) added - checking prhcomics.com for additional covers/variants...");
            FetchAdditionalCoversForImportedEntries(entries, allSeries, result, trace, onProgress);
        }

        trace.AppendLine();
        if (result.Added.Count > 0)
        {
            trace.AppendLine("Added:");
            foreach (var line in result.Added) trace.AppendLine("  " + line);
            trace.AppendLine();
        }
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {(saved ? result.NewIssuesAdded : 0)} new issue(s) added.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke(saved
            ? $"Done - {result.NewIssuesAdded} new issue(s) added, {result.CoverVariantsAdded} additional cover variant(s) found and {result.CoversBackfilled} cover image(s) pulled straight from PRH's own site. Anything PRH doesn't have yet still falls back to Comic Vine auto-linking."
            : $"Done - save failed, nothing from this file was added: {saveError}");
        return result;
    }

    // Automated counterpart to ImportPrhCatalogRelease above - added
    // 2026-11-25/26 after confirming, step by step in a real browser
    // (see claude/metron-integration-notes.md for the full write-up),
    // exactly what prhcomics.com's own "Download Excel Spreadsheet"
    // button does: a four-step WordPress-AJAX-plus-nonce chain ending in
    // a POST to a separate REST microservice (sales-docgen.prh.com) that
    // hands back the actual .xlsx, base64-encoded - see
    // PrhWebsiteFetcher's own class comment (bottom of this file) for
    // the full chain. Confirmed live that NONE of it needs a PRH
    // account or login (window.prhbl_user_is_logged_in was false on a
    // totally anonymous page load that still completed the whole
    // chain) - so, same as FetchLunarReleaseFromWebsite above, this
    // needs nothing but a plain HttpClient, no Playwright/browser
    // window.
    //
    // catalogCode is PRH's own "YYYY-MM" solicitation-month code (e.g.
    // "2026-12" for the catalog covering December 2026's FOC/on-sale
    // dates) - there's no single "current" catalog to discover
    // automatically the way Lunar's homepage exposes one (PRH's catalog
    // list page just links to several months at once), so the caller
    // passes the month it wants rather than this method guessing one.
    public AutoFetchResult FetchPrhCatalogFromWebsite(string catalogCode, Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"PRH website fetch run at {DateTime.Now:yyyy-MM-dd HH:mm:ss} (catalog {catalogCode})");
        trace.AppendLine(new string('=', 60));

        FetchOnePrhCatalog(catalogCode, result, trace, onProgress);

        trace.AppendLine(new string('=', 60));
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {result.NewIssuesAdded} new issue(s) added.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke(result.Saved
            ? $"Done - {result.NewIssuesAdded} new issue(s) added from the PRH {catalogCode} catalog."
            : $"Done - couldn't complete the PRH {catalogCode} import.");
        return result;
    }

    // Downloads and imports one catalog code, merging into the caller's
    // running result/trace rather than returning its own - same shape as
    // FetchOneLunarFile above, shared between the single-month method
    // just above and FetchAllAvailablePrhCatalogsFromWebsite below so
    // neither one duplicates the download+import logic. Returns whether
    // this catalog's data actually made it into the database (false on
    // either a fetch failure or a save failure), same meaning as
    // FetchOneLunarFile's own return value.
    private bool FetchOnePrhCatalog(string catalogCode, AutoFetchResult result, System.Text.StringBuilder trace, Action<string>? onProgress)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"prh_{catalogCode.Replace('-', '_')}_{Guid.NewGuid():N}.xlsx");
        try
        {
            byte[] bytes;
            try
            {
                bytes = PrhWebsiteFetcher.DownloadCatalogXlsxAsync(catalogCode, onProgress).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var line = $"Couldn't fetch the PRH {catalogCode} catalog from prhcomics.com: {ex.Message}";
                result.Errors.Add(line);
                trace.AppendLine(line);
                return false;
            }

            File.WriteAllBytes(tempPath, bytes);
            trace.AppendLine($"Downloaded catalog {catalogCode} ({bytes.Length:N0} bytes).");

            var sub = ImportPrhCatalogRelease(tempPath, onProgress);
            result.NewIssuesAdded += sub.NewIssuesAdded;
            result.CoversBackfilled += sub.CoversBackfilled;
            result.Added.AddRange(sub.Added);
            result.Errors.AddRange(sub.Errors);
            result.Saved = sub.Saved;
            trace.AppendLine(sub.Trace);
            return sub.Saved;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup only */ }
        }
    }

    // Fetches every catalog prhcomics.com's own public archive currently
    // lists, not just the one month FetchPrhCatalogFromWebsite above
    // covers - added 2026-11-26 after the store asked for all available
    // months, not just one at a time. Discovers the real list via
    // PrhWebsiteFetcher.DiscoverAvailableCatalogCodesAsync (see its own
    // doc comment) rather than guessing a date range, then fetches each
    // one in turn through the same FetchOnePrhCatalog helper the single-
    // month method uses - same relationship BackfillAllLunarHistory has
    // to FetchOneLunarFile above. Confirmed live 2026-11-26 the real
    // archive currently runs from 2021-06 (PRH's own comics-distribution
    // start) through whatever month is newest right now - that's 60+
    // catalogs, each its own full download+import, so this is a
    // genuinely long-running operation; same "run by hand, never on a
    // timer" reasoning as BackfillAllLunarHistory.
    // FetchAllHistoryBothDistributors checks for this prefix to tell
    // "PRH unreachable, nothing was checked" apart from per-book errors.
    private const string PrhArchiveUnreachablePrefix = "Couldn't read prhcomics.com's catalog archive listing:";

    // Non-null only while FetchAllHistoryBothDistributors runs: issue Ids
    // whose per-import PRH book-page check didn't complete (network error,
    // or the check stopped early), so its step 3 retries just those instead
    // of re-asking PRH about every Lunar-only book in the database.
    private HashSet<int>? _prhPageChecksPending;

    public AutoFetchResult FetchAllAvailablePrhCatalogsFromWebsite(Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"PRH full-history website fetch run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        // This one request gates the whole fetch, so a short Wi-Fi blip at
        // this exact moment used to lose everything (2026-11-27). Retry
        // network failures a few times before giving up; a real HTTP
        // error from the site is not retried.
        List<string> codes;
        try
        {
            codes = null!;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    codes = PrhWebsiteFetcher.DiscoverAvailableCatalogCodesAsync(onProgress).GetAwaiter().GetResult();
                    break;
                }
                catch (Exception ex) when (attempt < 3 && PrhWebsiteFetcher.IsNetworkFailure(ex))
                {
                    var retryLine = $"Couldn't reach prhcomics.com ({ex.Message}) - retrying in 10s (attempt {attempt} of 3)...";
                    trace.AppendLine(retryLine);
                    onProgress?.Invoke(retryLine);
                    Thread.Sleep(10_000);
                }
            }
        }
        catch (Exception ex)
        {
            var line = $"{PrhArchiveUnreachablePrefix} {ex.Message}";
            result.Errors.Add(line);
            trace.AppendLine(line);
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            onProgress?.Invoke($"Done - couldn't reach prhcomics.com: {ex.Message}");
            return result;
        }

        if (codes.Count == 0)
        {
            var line = "Found no available catalogs on prhcomics.com's archive listing - its page layout may have changed since this was built.";
            result.Errors.Add(line);
            trace.AppendLine(line);
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            onProgress?.Invoke("Done - found no available catalogs.");
            return result;
        }

        trace.AppendLine($"Found {codes.Count} available catalog(s): {string.Join(", ", codes)}");
        onProgress?.Invoke($"Found {codes.Count} available PRH catalog(s) - fetching each one...");

        var catalogsOk = 0;
        var catalogsFailed = 0;
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            onProgress?.Invoke($"Fetching PRH catalog {code} ({i + 1} of {codes.Count})...");
            if (FetchOnePrhCatalog(code, result, trace, onProgress))
                catalogsOk++;
            else
                catalogsFailed++;
        }

        trace.AppendLine(new string('=', 60));
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {catalogsOk} catalog(s) fetched cleanly, {catalogsFailed} had issues. {result.NewIssuesAdded} new issue(s) added total.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke($"Done - {codes.Count} catalog(s) processed, {result.NewIssuesAdded} new issue(s) added total.");
        return result;
    }

    // Automated counterpart to ImportLunarDistributionRelease above -
    // added 2026-11-24 once it was confirmed (see
    // metron-integration-notes.md) that lunardistribution.com's own
    // homepage is plain server-rendered HTML (no JavaScript needed to
    // read it) that already embeds, as two <button class="datafile
    // release"/"datafile foc" data-val="MM/dd/yyyy h:mm:ss AM/PM"> tags,
    // the exact dates for "this week's release file" and "the current
    // FOC-cutoff file" - and that the actual file download
    // (/home/productdatafile?release=<date> or ?foc=<date>) needs no
    // cookies, session or login at all: a bare anonymous GET, verified
    // with credentials deliberately stripped, still returns a real
    // .xlsx with correct Content-Disposition/Content-Type/Content-Length
    // headers. So this needs nothing but a plain HttpClient - no
    // Playwright, no browser window, nothing Cloudflare-guarded like
    // LOCG - to do exactly what clicking both "Product Data File"
    // buttons on the homepage and uploading each result would do by
    // hand: read the homepage once to find today's two current dates,
    // download both files the same way the site's own buttons do, and
    // run each one through ImportLunarDistributionRelease (reusing 100%
    // of its parsing/image-saving/AddEntriesAsIssues logic - the only
    // thing this adds is how the .xlsx bytes are obtained).
    //
    // Deliberately NOT part of the weekly automatic CheckForNewIssues
    // rotation (unlike Metron) - the homepage only ever exposes ONE
    // current release date and ONE current FOC date at a time (see the
    // "previous"/"next" date-nav arrows sitting right next to each
    // button in the same HTML), so running this on a timer between the
    // store's own weekly visits would just re-download that same single
    // week's file repeatedly rather than discovering anything new. This
    // is a one-click stand-in for the manual download+upload step, meant
    // to be run by hand whenever the store wants this week's file - same
    // onProgress/threading contract as every other method here (assume
    // nothing about which thread calls this on).
    public AutoFetchResult FetchLunarReleaseFromWebsite(Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Lunar Distribution website fetch run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        string html;
        try
        {
            onProgress?.Invoke("Reading lunardistribution.com's homepage for this week's current release/FOC dates...");
            html = LunarWebsiteFetcher.Http.GetStringAsync("https://www.lunardistribution.com/").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var line = $"Couldn't reach lunardistribution.com: {ex.Message}";
            result.Errors.Add(line);
            trace.AppendLine(line);
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            return result;
        }

        var releaseDate = LunarWebsiteFetcher.FindDataVal(html, "release");
        var focDate = LunarWebsiteFetcher.FindDataVal(html, "foc");

        if (releaseDate == null && focDate == null)
        {
            var line = "Couldn't find the site's current release/FOC date buttons on the homepage - lunardistribution.com's page layout may have changed since this was built.";
            result.Errors.Add(line);
            trace.AppendLine(line);
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            return result;
        }

        if (releaseDate != null)
            FetchOneLunarFile("release", releaseDate, result, trace, onProgress);
        if (focDate != null)
            FetchOneLunarFile("foc", focDate, result, trace, onProgress);

        trace.AppendLine(new string('=', 60));
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {result.NewIssuesAdded} new issue(s) added total.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke($"Done - {result.NewIssuesAdded} new issue(s) added total.");
        return result;
    }

    // Downloads one file (kind is "release" or "foc") for the given
    // site-supplied date-val string, saves it to a throwaway temp path,
    // and runs it through ImportLunarDistributionRelease - merging that
    // sub-result into the running total rather than returning it on its
    // own, so FetchLunarReleaseFromWebsite can report both files' results
    // as a single combined number instead of asking the caller to add
    // two results together itself.
    // Returns whether this file's data actually made it into the database -
    // added 2026-11-24 (previously void) so WalkAndImportLunarDates' own
    // "weeks imported" count only counts a REAL success instead of every
    // attempt (see that method's own counter - it used to increment
    // regardless of whether the save underneath actually worked, which is
    // how a real backfill run's trace ended up claiming "109 weeks
    // imported" when only 5 actually were). FetchLunarReleaseFromWebsite
    // itself still ignores the return value - it already reports success
    // via NewIssuesAdded/Errors the way it always has, nothing about that
    // one-shot path needed to change.
    private bool FetchOneLunarFile(string kind, string dateVal, AutoFetchResult result, System.Text.StringBuilder trace, Action<string>? onProgress)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"lunar_{kind}_{Guid.NewGuid():N}.xlsx");
        try
        {
            onProgress?.Invoke($"Downloading this week's {kind} file ({dateVal})...");
            var url = $"https://www.lunardistribution.com/home/productdatafile?{kind}={Uri.EscapeDataString(dateVal)}";
            var bytes = LunarWebsiteFetcher.Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(tempPath, bytes);
            trace.AppendLine($"Downloaded {kind} file for {dateVal} ({bytes.Length:N0} bytes).");

            var sub = ImportLunarDistributionRelease(tempPath, onProgress);
            result.NewIssuesAdded += sub.NewIssuesAdded;
            result.CoversBackfilled += sub.CoversBackfilled;
            result.Added.AddRange(sub.Added);
            result.Errors.AddRange(sub.Errors);
            trace.AppendLine(sub.Trace);
            return sub.Saved;
        }
        catch (Exception ex)
        {
            var line = $"{kind} file ({dateVal}) failed: {ex.Message}";
            result.Errors.Add(line);
            trace.AppendLine(line);
            return false;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup only */ }
        }
    }

    // Full historical backfill, added 2026-11-24 after FetchLunarReleaseFromWebsite
    // above (which only ever grabs the ONE current week in each direction) -
    // the store asked for "as far back as it goes... then as far into the
    // future as possible". Confirmed live against lunardistribution.com
    // (see metron-integration-notes.md for the full investigation) that the
    // homepage's "previous"/"next" date-nav arrows are driven by two small
    // JSON AJAX endpoints the site's own jQuery calls on click:
    //   POST /home/instoreproducts  { body: JSON-encoded "M/d/yyyy" string }
    //   POST /home/focproducts      { body: JSON-encoded "M/d/yyyy" string }
    // Each returns { success, products[], previous, next, current } for a
    // real catalog date, or a bare {"success":true} with none of those
    // other fields for a date with nothing on file - confirmed as the one
    // real "nothing here" signal (rather than guessing from downloaded
    // file size, which was the first approach tried and would have worked
    // but needed a full file download just to tell "empty" from "real").
    // Also confirmed /home/productdatafile itself accepts the exact same
    // plain "M/d/yyyy" string these nav endpoints use (not just the
    // "...12:00:00 AM" form the homepage's own buttons carry), so this
    // walks and downloads using one single date-string format throughout.
    //
    // Walking backward via "previous" from today originally looked like it
    // found the server's own built-in floor live: the oldest release date
    // reached (2025-11-18 at the time this was first written) reports
    // "1/1/0001" as ITS OWN previous - .NET's DateTime.MinValue. CORRECTED
    // 2026-11-27 (the store found this by hand - see
    // full-reset-2026-11-27.md): that sentinel is NOT "nothing earlier
    // exists." Confirmed live against dozens of real historical dates
    // spanning 2025 all the way back to 2013 that lunardistribution.com
    // returns that exact same fixed pair - previous "1/1/0001", next
    // "12/2/2025" - for EVERY date before 2025-12-02, no matter which one
    // is actually requested. The real explanation: the site only keeps a
    // genuine previous/next CHAIN for "live" dates (2025-12-02 onward);
    // everything older sits in a flat archive with no chain at all, but
    // still answers with real product data when a specific date is asked
    // for directly - confirmed real, non-empty results as far back as
    // Lunar Distribution's own first week of business, April 2020 (the
    // company didn't exist before then - it was formed in about three
    // weeks after Diamond Comic Distributors' COVID shutdown, per
    // Lunar co-founder Christina Merkler's own account). So
    // WalkAndImportLunarDates below has two phases for the backward
    // (release) direction: follow the server's own chain while it's real,
    // then - once that fixed sentinel shows up - switch to self-computed
    // date stepping (Lunar only ever has entries on Tuesdays and
    // Wednesdays, confirmed live in both regimes) down to Lunar's own
    // 2020-04-01 launch floor, rather than stopping the moment the chain
    // itself gives out. Walking forward via "next" on the FOC side still
    // ran out around mid-2027 before jumping to a "12/31/2039" far-future
    // sentinel - FOC has no archive at all before the live window
    // (confirmed empty for every pre-2025-12-02 date tried), so the
    // forward direction doesn't need this two-phase treatment. Both
    // sentinels are detected by parsed year rather than an exact string
    // match (see WalkAndImportLunarDates below), in case the exact
    // sentinel value ever changes on their end.
    //
    // One wrinkle confirmed live: the server's own previous/next chain
    // isn't perfectly gapless even within the live regime - once, a
    // "previous" pointer led to a dead date that itself came back as the
    // bare no-fields response, even though real data existed even further
    // back. Simply stopping there would have silently truncated the
    // backfill early, so the walk below recovers from a dead date by
    // stepping past the gap (bounded by a dead-streak limit) until it
    // finds a real date again, then resumes from there - confirmed live
    // that this correctly steps across the one gap found during testing
    // (which spanned about a week of dead dates). The same recovery
    // applies once self-computed stepping through the archive begins,
    // using the archive's own Tuesday/Wednesday cadence instead of a
    // plain day-at-a-time step.
    //
    // This can mean hundreds of file downloads+imports in one run (over
    // six years of twice-weekly history on the release side alone), so it
    // reuses FetchOneLunarFile's existing per-file error handling (one bad
    // week's failure doesn't stop the rest) and reports progress once per
    // week like BackfillYearFromLocg/CheckForNewIssuesViaMetron do. Meant
    // to be run once (or occasionally, to pick up newly-published future
    // weeks) by hand from NewBooks.razor - never on a timer, same
    // reasoning as FetchLunarReleaseFromWebsite above.
    public AutoFetchResult BackfillAllLunarHistory(Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Lunar full-history backfill run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        string html;
        try
        {
            onProgress?.Invoke("Reading lunardistribution.com's homepage for today's current release/FOC dates...");
            html = LunarWebsiteFetcher.Http.GetStringAsync("https://www.lunardistribution.com/").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var line = $"Couldn't reach lunardistribution.com: {ex.Message}";
            result.Errors.Add(line);
            trace.AppendLine(line);
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            return result;
        }

        var releaseDate = LunarWebsiteFetcher.FindDataVal(html, "release");
        var focDate = LunarWebsiteFetcher.FindDataVal(html, "foc");

        if (releaseDate == null && focDate == null)
        {
            var line = "Couldn't find the site's current release/FOC date buttons on the homepage - lunardistribution.com's page layout may have changed since this was built.";
            result.Errors.Add(line);
            trace.AppendLine(line);
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            return result;
        }

        // The datafile buttons' own data-val carries a "...12:00:00 AM"
        // time suffix (see LunarWebsiteFetcher.FindDataVal) - the nav
        // endpoints want the plain date with no time, so just the part
        // before the first space is kept as the walk's starting point.
        var releaseStart = releaseDate?.Split(' ')[0];
        var focStart = focDate?.Split(' ')[0];

        // Generous safety caps, not real expected counts - the
        // dead-streak/sentinel/archive-floor checks inside the walk
        // itself are what actually stop it under normal conditions long
        // before either of these would ever be hit.
        //
        // The FOC (forward) side never gained the archive-walk below - it
        // has no pre-live archive at all (confirmed empty for every
        // pre-2025-12-02 date tried) - so its cap stays at the original
        // live-chain-only estimate (~65 steps observed, ~6x headroom).
        const int focMaxSteps = 400;

        // The release (backward) side now also self-steps through the
        // archive down to Lunar's own 2020-04-01 launch floor (see the
        // long doc comment above) once the live chain's sentinel shows
        // up: roughly 6.7 years of TWO entries/week (confirmed - Tuesday
        // and Wednesday both carry real data) is on the order of 700
        // steps; this leaves close to 2x headroom over that estimate.
        const int releaseMaxSteps = 1400;

        // Lunar's own first week of business - co-founder Christina
        // Merkler's own account (icv2 interview) gives the first shipment
        // as "04/'20." A small buffer below that costs almost nothing (a
        // handful of extra empty-date checks) and guards against the
        // launch-week date being slightly fuzzier than one exact day.
        var archiveFloor = new DateOnly(2020, 3, 15);

        if (releaseStart != null)
        {
            onProgress?.Invoke("Walking backward through Lunar's release history as far as it goes (including the pre-2025-12-02 archive, down to Lunar's own 2020 launch)...");
            WalkAndImportLunarDates("/home/instoreproducts", "release", releaseStart, direction: -1, releaseMaxSteps, result, trace, onProgress, archiveFloor);
        }

        if (focStart != null)
        {
            onProgress?.Invoke("Walking forward through Lunar's FOC solicitations as far as they go...");
            WalkAndImportLunarDates("/home/focproducts", "foc", focStart, direction: +1, focMaxSteps, result, trace, onProgress);
        }

        trace.AppendLine(new string('=', 60));
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {result.NewIssuesAdded} new issue(s) added total.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke($"Done - {result.NewIssuesAdded} new issue(s) added total across all of Lunar's available history.");
        return result;
    }

    // Added 2026-11-27 - the store asked for "one button that gets both
    // [Lunar and PRH], all the way back to when they first started doing
    // solicits... deal with duplicates between the two distributors
    // accordingly." This chains the two existing full-history methods
    // together, in a FIXED order, never concurrently:
    //
    // 1. BackfillAllLunarHistory (above) runs to completion first.
    // 2. FetchAllAvailablePrhCatalogsFromWebsite runs second.
    // 3. BackfillCoverVariantsFromPrhBookPages runs last, as a catch-up
    //    pass for any PRH issue whose own book page didn't have every
    //    sibling cover listed yet at the exact moment it was imported in
    //    step 2 (same reasoning BackfillCoverVariantsFromPrhBookPages's
    //    own doc comment already gives for why it's useful as a repeat-
    //    able catch-up, not just a one-time tool).
    //
    // Running Lunar fully to completion BEFORE PRH starts is what makes
    // the "deal with duplicates accordingly" half of the request work for
    // free, with no new matching code: both ImportLunarDistributionRelease
    // and ImportPrhCatalogRelease already route every row through the one
    // shared AddEntriesAsIssues, which looks up a Series by
    // SeriesNameKey.Normalize(...) before ever creating a new one. So
    // whichever distributor's import reaches a given series name FIRST is
    // the one whose Series row sticks - the other distributor's issues
    // just attach onto that same existing row instead of creating a
    // second one. That's exactly "choose the first one that appeared,"
    // the store's own call on how to handle this, with zero new series-
    // matching logic needed - just a fixed run order against the same
    // live database (if this ran Lunar and PRH concurrently instead, two
    // brand-new series rows could get created for the same real series
    // at nearly the same moment, racing each other - sequential order is
    // what rules that out entirely, not just makes it less likely).
    //
    // Ceiling per distributor, not a bug: Lunar's release-side walk now
    // reaches all the way back to Lunar Distribution's own 2020-04-01
    // launch (see BackfillAllLunarHistory's own doc comment above for the
    // full two-regime discovery and why the site's "1/1/0001" sentinel
    // doesn't mean what it first looked like it meant). FOC has no
    // archive before the live window at all, so its ceiling stays at
    // whatever the site's own live chain covers. PRH's own archive goes
    // back to 2021-06 (see DiscoverAvailableCatalogCodesAsync's own doc
    // comment). "All the way back to when they first started doing
    // solicits" means three different real floors (Lunar release, Lunar
    // FOC, PRH) - this reaches all of them; none is a shortcut taken by
    // this code, each is the genuine floor on that distributor's own
    // server.
    //
    // Each of the three steps already reports its own progress and
    // writes its own full trace to checknow-debug.txt; this wraps all
    // three into one combined AutoFetchResult/trace so the caller (a
    // single new button on NewBooks.razor) can show one combined summary
    // at the end instead of three separate popups.
    public AutoFetchResult FetchAllHistoryBothDistributors(Action<string>? onProgress = null)
    {
        var combined = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Combined Lunar+PRH full-history fetch run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        void Merge(AutoFetchResult part, string label)
        {
            combined.SeriesChecked += part.SeriesChecked;
            combined.SeriesAutoLinked += part.SeriesAutoLinked;
            combined.NewIssuesAdded += part.NewIssuesAdded;
            combined.CoversBackfilled += part.CoversBackfilled;
            combined.CoverVariantsAdded += part.CoverVariantsAdded;
            combined.Added.AddRange(part.Added);
            combined.Errors.AddRange(part.Errors);
            if (!part.Saved) combined.Saved = false;
            trace.AppendLine(new string('-', 60));
            trace.AppendLine($"[{label}]");
            trace.AppendLine(part.Trace);
        }

        onProgress?.Invoke("Step 1 of 3: Backfilling Lunar's full history (as far back as lunardistribution.com itself goes)...");
        var lunar = BackfillAllLunarHistory(progressLine => onProgress?.Invoke($"Lunar: {progressLine}"));
        Merge(lunar, "Lunar full-history backfill");

        onProgress?.Invoke("Step 2 of 3: Fetching every available PRH catalog (2021-06 through now)...");
        AutoFetchResult prh;
        _prhPageChecksPending = new HashSet<int>();
        HashSet<int> pending;
        try
        {
            prh = FetchAllAvailablePrhCatalogsFromWebsite(progressLine => onProgress?.Invoke($"PRH: {progressLine}"));
        }
        finally
        {
            pending = _prhPageChecksPending;
            _prhPageChecksPending = null;
        }
        Merge(prh, "PRH full-history catalog fetch");

        // Step 2's per-import check already covered every issue in every PRH
        // catalog, so step 3 only retries the ones whose check didn't finish -
        // not a whole-database pass (~4,800 Lunar-only books PRH never sells).
        trace.AppendLine(new string('-', 60));
        if (prh.Errors.Any(e => e.StartsWith(PrhArchiveUnreachablePrefix)))
        {
            var skipLine = "[PRH cover-variant retry] SKIPPED - prhcomics.com couldn't be reached in step 2, so there's nothing to retry yet. Run Fetch All Available PRH Catalogs once you're back online.";
            trace.AppendLine(skipLine);
            onProgress?.Invoke(skipLine);
        }
        else if (pending.Count == 0)
        {
            trace.AppendLine("[PRH cover-variant retry] Nothing to retry - every PRH book-page check in step 2 completed.");
        }
        else
        {
            onProgress?.Invoke($"Step 3 of 3: Retrying {pending.Count} PRH cover-variant check(s) that didn't finish above...");
            var retry = BackfillCoverVariantsFromPrhBookPages(progressLine => onProgress?.Invoke($"PRH cover retry: {progressLine}"), null, pending);
            Merge(retry, "PRH cover-variant retry");
        }

        trace.AppendLine(new string('=', 60));
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. {combined.NewIssuesAdded} new issue(s) added, {combined.CoverVariantsAdded} new cover variant(s) added, {combined.CoversBackfilled} barcode(s) upgraded - combined across both distributors' full history.");
        combined.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(combined.Trace);
        onProgress?.Invoke($"Done - {combined.NewIssuesAdded} new issue(s) and {combined.CoverVariantsAdded} new cover(s) added across Lunar + PRH's full history.");
        return combined;
    }

    // One direction of the walk described in BackfillAllLunarHistory's own
    // doc comment above - direction -1 walks backward using each
    // response's "previous" (release/in-store history), +1 walks forward
    // using "next" (FOC solicitations). Shared between both directions
    // since the logic (gap recovery, sentinel detection, per-week import)
    // is identical; only the AJAX path, the productdatafile query key, and
    // which of previous/next to follow differ.
    private void WalkAndImportLunarDates(
        string navPath,
        string fileQueryKey,
        string startDate,
        int direction,
        int maxSteps,
        AutoFetchResult result,
        System.Text.StringBuilder trace,
        Action<string>? onProgress,
        DateOnly? archiveFloor = null)
    {
        var current = startDate;
        var deadStreak = 0;

        // Once true, the server's own previous/next can no longer be
        // trusted at all (see the archive discovery in the big doc
        // comment on BackfillAllLunarHistory) - every remaining step
        // computes its own next candidate date instead. Only ever set
        // for the backward (release) direction, and only when a caller
        // actually passed an archiveFloor (the forward FOC walk never
        // enters this mode - it has no pre-live archive to fall into).
        var inArchiveMode = false;

        // One real gap found during live testing spanned about a week of
        // consecutive dead dates before the chain picked back up; this
        // gives 3x margin above that without walking forever into
        // genuinely empty territory once history (or the future horizon)
        // really does end.
        const int deadStreakLimit = 21;
        var weeksImported = 0;

        for (var step = 0; step < maxSteps; step++)
        {
            LunarNavResult nav;
            try
            {
                nav = PostLunarNav(navPath, current);
            }
            catch (Exception ex)
            {
                trace.AppendLine($"{navPath} lookup for {current} failed: {ex.Message} - stopping this direction.");
                break;
            }

            if (!nav.HasNav)
            {
                deadStreak++;
                trace.AppendLine($"{current}: no catalog data here (dead/gap date).");
                if (deadStreak > deadStreakLimit)
                {
                    trace.AppendLine($"{deadStreakLimit} consecutive empty day(s) - treating this as the real end of {(direction < 0 ? "history" : "the solicitation horizon")}.");
                    break;
                }

                if (!DateOnly.TryParseExact(current, "M/d/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dead))
                {
                    trace.AppendLine($"Couldn't parse '{current}' as a date to step past the gap - stopping.");
                    break;
                }

                if (inArchiveMode)
                {
                    // Keep stepping on the archive's own Tuesday/Wednesday
                    // cadence rather than a plain day-at-a-time step,
                    // which would waste calls on days that structurally
                    // never have data in either regime.
                    var archiveGapNext = PreviousLunarCandidate(dead);
                    if (archiveFloor.HasValue && archiveGapNext < archiveFloor.Value)
                    {
                        trace.AppendLine($"Reached the archive floor ({archiveFloor.Value:M/d/yyyy}) - stopping.");
                        break;
                    }
                    current = FormatLunarDate(archiveGapNext);
                }
                else
                {
                    current = FormatLunarDate(dead.AddDays(direction));
                }
                continue;
            }

            deadStreak = 0;

            if (nav.ProductCount > 0)
            {
                onProgress?.Invoke($"{(direction < 0 ? "Backfilling" : "Forward-filling")} {current} ({nav.ProductCount} item(s))...");
                // Only counts a REAL success (see FetchOneLunarFile's own
                // doc comment) - used to increment unconditionally here,
                // which is how a real run's trace ended up claiming "109
                // week(s) imported" when a cascading SaveChanges failure
                // (see TrySaveChanges) meant only 5 of them actually were.
                if (FetchOneLunarFile(fileQueryKey, current, result, trace, onProgress))
                    weeksImported++;
            }
            else
            {
                trace.AppendLine($"{current}: real catalog date, 0 items.");
            }

            if (!DateOnly.TryParseExact(current, "M/d/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var currentParsed))
            {
                trace.AppendLine($"Couldn't parse '{current}' to decide the next step - stopping.");
                break;
            }

            if (inArchiveMode)
            {
                // Already past the live chain - an archive date's own
                // previous/next are the same fixed, useless pair every
                // time (see the doc comment on BackfillAllLunarHistory),
                // so don't even look at them; just keep self-stepping.
                var nextArchiveCandidate = PreviousLunarCandidate(currentParsed);
                if (archiveFloor.HasValue && nextArchiveCandidate < archiveFloor.Value)
                {
                    trace.AppendLine($"Reached the archive floor ({archiveFloor.Value:M/d/yyyy}) - stopping.");
                    break;
                }
                current = FormatLunarDate(nextArchiveCandidate);
                continue;
            }

            var onward = direction < 0 ? nav.Previous : nav.Next;
            if (string.IsNullOrEmpty(onward))
            {
                trace.AppendLine($"{current}: no further {(direction < 0 ? "previous" : "next")} date - stopping.");
                break;
            }

            // The server's own floor/ceiling sentinel - .NET
            // DateTime.MinValue ("1/1/0001") going backward, and a
            // deliberately-far placeholder ("12/31/2039" at the time this
            // was tested) going forward. Checked by parsed year range
            // rather than an exact string match, in case the exact
            // sentinel value ever changes on their end.
            if (!DateOnly.TryParseExact(onward, "M/d/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var onwardParsed)
                || onwardParsed.Year is < 2000 or > 2100)
            {
                // CORRECTED 2026-11-27: for the backward release walk,
                // this is NOT the real end of history - it's the known
                // flat-archive sentinel (see the long doc comment on
                // BackfillAllLunarHistory). Switch to self-computed
                // Tuesday/Wednesday stepping from the last date that DID
                // have real data, instead of stopping here.
                if (direction < 0 && archiveFloor.HasValue)
                {
                    trace.AppendLine($"{current}: server's own chain ends here (reports a fixed '{onward}' for every archive date - not a real floor, see full-reset-2026-11-27.md). Switching to direct date-stepping to keep going through the archive.");
                    inArchiveMode = true;
                    var firstArchiveCandidate = PreviousLunarCandidate(currentParsed);
                    if (firstArchiveCandidate < archiveFloor.Value)
                    {
                        trace.AppendLine($"Reached the archive floor ({archiveFloor.Value:M/d/yyyy}) - stopping.");
                        break;
                    }
                    current = FormatLunarDate(firstArchiveCandidate);
                    continue;
                }

                trace.AppendLine($"Reached the site's own end-of-data marker ({onward}) - stopping.");
                break;
            }

            current = onward;
        }

        trace.AppendLine($"{(direction < 0 ? "Backward" : "Forward")} walk finished: {weeksImported} week(s) with real data imported.");
    }

    private static string FormatLunarDate(DateOnly d) => $"{d.Month}/{d.Day}/{d.Year}";

    // Steps from a known Tuesday or Wednesday catalog date to the
    // previous one in Lunar's own two-entry-per-week pattern - confirmed
    // live across both the archive (back to 2020-03) and the live regime
    // that these are the ONLY two days that ever carry data: Wednesday
    // steps back 1 day to that same week's Tuesday; Tuesday steps back 6
    // days to the previous week's Wednesday. Only used once the archive
    // fallback in WalkAndImportLunarDates kicks in, where the server's
    // own previous/next can't be trusted at all.
    private static DateOnly PreviousLunarCandidate(DateOnly d) =>
        d.DayOfWeek == DayOfWeek.Wednesday ? d.AddDays(-1) : d.AddDays(-6);

    private sealed class LunarNavResult
    {
        // True only when the response carried a "previous" field at all -
        // the one real signal (confirmed live) that this was a real
        // catalog date rather than the bare {"success":true} a dead/gap
        // date returns.
        public bool HasNav;
        public int ProductCount;
        public string? Previous;
        public string? Next;
    }

    // POSTs a plain "M/d/yyyy" date string (JSON-encoded, matching exactly
    // what the site's own jQuery sends - see the big comment on
    // BackfillAllLunarHistory above for how this was confirmed) to one of
    // the site's two small nav AJAX endpoints and reads back whichever of
    // success/products/previous/next it included.
    private static LunarNavResult PostLunarNav(string path, string dateVal)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(dateVal);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = LunarWebsiteFetcher.Http.PostAsync("https://www.lunardistribution.com" + path, content).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        var hasNav = root.TryGetProperty("previous", out var prevEl);
        var count = 0;
        if (root.TryGetProperty("products", out var productsEl) && productsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            count = productsEl.GetArrayLength();

        return new LunarNavResult
        {
            HasNav = hasNav,
            ProductCount = count,
            Previous = hasNav ? prevEl.GetString() : null,
            Next = hasNav && root.TryGetProperty("next", out var nextEl) ? nextEl.GetString() : null
        };
    }

    // A looser match than SeriesNameKey.Normalize - ALSO strips a trailing
    // "(YYYY)"/"(YYYY-)" volume-year marker, same pattern as
    // LunarTitleParser.VolumeYearMarker (SeriesNameKey.Normalize itself
    // strips a leading "The " plus colon/dash subtitle-separator noise as
    // of 2026-11-24 - see its own doc comment - but deliberately does NOT
    // touch a trailing year, since that one CAN be a real disambiguator
    // between two genuinely different volumes that happen to share a
    // title). ONLY used by FindDuplicateSeries/MergeDuplicateSeries
    // below, to spot the specific split the 2026-11-24 Lunar
    // "(YYYY)"-before-the-issue-number parser bug could produce (a series
    // ending up both as "Name" and "Name (2024)" as two separate rows) -
    // deliberately NOT folded into SeriesNameKey.Normalize itself, since
    // that key decides whether AddEntriesAsIssues creates a brand-new
    // series at all, and two DIFFERENT real volumes that happen to share
    // a title (genuinely disambiguated by their year on purpose) should
    // still stay separate there. This merge tool is a one-time cleanup
    // scoped to the known bug, not a permanent change to how series are
    // matched going forward.
    private static readonly Regex DuplicateSeriesYearSuffix = new(@"\s*\(\s*(?:19|20)\d{2}-?\s*\)\s*$", RegexOptions.Compiled);

    // Widened 2026-11-26 after the user found a whole category of real
    // duplicates this key was missing entirely: PRH's own collected-
    // edition (TR/HC/NT format) titles bake the format and a volume
    // marker directly into the title text, with no consistent spelling -
    // confirmed live against real New Books rows for the exact same
    // underlying book:
    //   "Absolute Infinite Crisis (New Edition) (2026)"
    //   "ABSOLUTE INFINITE CRISIS HC (2026 EDITION) (2026)"
    // and
    //   "ABSOLUTE SUPERMAN HC VOL 03 THE NEVERENDING BEGINS (2026)"
    //   "ABSOLUTE SUPERMAN TP VOL 03 THE NEVERENDING BEGINS (2026)"
    //   "Absolute Superman Vol. 3: The Neverending Begins (2026)"
    // None of these share an exact SeriesName - PrhTitleParser.ApplyTitle
    // deliberately does NOT try to strip format/volume wording out of a
    // collected edition's title (see its own doc comment - PRH's format
    // info comes from a separate column, not guessed from text), so the
    // whole raw title ends up as SeriesName verbatim, and three
    // differently-worded solicitations of the very same book become
    // three different Series rows that this key never used to catch.
    //
    // Three extra passes, same "looser than SeriesNameKey.Normalize,
    // ONLY for this dry-run/merge pair" spirit as the year-suffix strip
    // above:
    //  - DuplicateSeriesEditionParenthetical strips any "(... Edition)"
    //    parenthetical note ("(New Edition)", "(2026 Edition)") - these
    //    only ever describe which printing/package a book is, never a
    //    different book. EXCEPT "(Facsimile Edition)" specifically
    //    (negative lookahead guards this) - confirmed live 2026-11-26 via
    //    a real "Find Duplicate Series" scan: this would otherwise have
    //    paired 38 real series (Action Comics, Batman, Watchmen, etc.)
    //    with their own "(Facsimile Edition)" counterpart as "duplicates"
    //    - undoing the whole point of the earlier Lunar facsimile-title
    //    fix (see that section further up this doc), which deliberately
    //    gives a facsimile reprint its OWN distinct series/issue so it
    //    never gets confused with a modern issue of the same number. A
    //    facsimile reprint of a classic issue is a genuinely different
    //    product from the current ongoing series sharing its name, never
    //    "just another printing" the way "(New Edition)"/"(2026
    //    Edition)" are - merging them would have filed, say, a facsimile
    //    of a decades-old key issue as "another cover" of today's current
    //    issue #1 of the same-named ongoing series.
    //  - DuplicateSeriesVolumeMarker normalizes "Vol 03"/"Vol. 3"/"VOLUME
    //    03" down to just the bare number (leading zeros dropped) -
    //    deliberately keeping the NUMBER itself, not stripping it: PRH's
    //    own collected-edition titles never carry a real "#N", so
    //    PrhTitleParser.ApplyTitle always gives them IssueNumber = 1
    //    regardless of which volume they actually are (see its own doc
    //    comment) - the volume number living on INSIDE the series name is
    //    the only thing in this whole app that tells "Vol. 3" apart from
    //    "Vol. 4" of the same franchise, so losing it here would merge
    //    two completely different books. Confirmed this can't happen:
    //    "Absolute Superman Vol. 3: ..." and a hypothetical "...Vol. 4:
    //    ..." still produce different keys ("3" vs "4").
    //
    // REVERSED 2026-11-26 (Bug #22): a third pass, DuplicateSeriesFormatWord,
    // used to also strip a bare HC/TP/SC/GN/OHC/OGN/TPB format token, on
    // the theory that an HC/TP/reprint trio of the same book was "the
    // same shape" as a single floppy issue's own Cover A/B/C variants
    // (see MergeDuplicateSeries' three-tier cover merge) and could merge
    // the same way. Live data proved that theory wrong: the user found a
    // real post-widening scan flagging 267 "duplicate" groups, the vast
    // majority of them genuine HC-vs-TP(-vs-plain) pairs of the exact
    // same book - e.g. "ABSOLUTE SUPERMAN HC VOL 03..." vs "...TP VOL
    // 03..." - correctly NAME-matched, but wrongly treated as mergeable:
    // an HC and a TP are two different real SKUs a comic shop stocks and
    // sells separately (different barcode, different price, independent
    // on-hand counts), not two printed covers of one interchangeable
    // product. Confirmed live and much worse than just "untidy": for any
    // group where an ongoing/limited floppy run happened to share this
    // key with its own later HC and TP (e.g. "DC K.O." - 5 real single
    // issues - grouped with "DC K.O. HC" and "DC K.O. TP", each 1 issue),
    // MergeDuplicateSeries' own Type-aware matching (Bug #19) does NOT
    // treat the HC and the TP as a conflict with the floppy run's issue
    // #1 (different Type), so each gets "moved" onto the floppy series
    // as its own new Issue row - except the HC and the TP share the same
    // (IssueNumber, Type) key AS EACH OTHER, so the one processed second
    // lands in the "conflict" branch and has ITS real barcode/price
    // merged in as just another lettered Cover on the first one's Issue
    // row. Net result if a merge had run: the hardcover and the
    // paperback - two genuinely different, separately-priced products -
    // would have collapsed into one anonymous "issue" nested under the
    // floppy run's series, both their own distinct catalog names gone.
    // Removed entirely rather than narrowed, at the time - see
    // DuplicateSeriesKey just below.
    //
    // RE-REVERSED 2026-11-26 (Bug #23, same day): removing this pass
    // entirely went too far the other way. The user confirmed live that
    // genuinely-safe HC/TP/plain trios of the SAME book (no complicating
    // floppy run at all, e.g. "Absolute Superman Vol. 3" solicited three
    // times by PRH, worded three different ways) stopped being found OR
    // even just listed once this strip was gone - not merged, not even
    // shown as "possible duplicates" to look at. The real fix was never
    // "never strip this" - it's keeping the strip (so these really do
    // still match as the same key), while separately gating the MERGE
    // action itself on format: DuplicateSeriesFormatBucket (below) and
    // GetDuplicateSeriesCandidateGroups now do that - see their own doc
    // comments. FindDuplicateSeries' scan uses no bucket gate at all (so
    // it shows everything, HC-vs-TP included); MergeDuplicateSeries only
    // auto-merges a group whose members are ALL the same bucket, and
    // explicitly logs - without touching - any group that isn't.
    private static readonly Regex DuplicateSeriesEditionParenthetical = new(@"\((?!\s*[^()]*\bfacsimile\b)[^()]*\bedition\b[^()]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DuplicateSeriesVolumeMarker = new(@"\bVol(?:ume)?\.?\s*0*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Bug #23 fix (2026-11-26): brought back - Bug #22 had removed this
    // entirely (see that fix's own history in the comment block above).
    // See DuplicateSeriesKey just below for why outright removal went
    // too far, and DuplicateSeriesFormatBucket further down for the new,
    // separate, merge-only safeguard that replaces what removing this
    // strip used to be the only way to get.
    private static readonly Regex DuplicateSeriesFormatWord = new(@"\b(?:HC|TP|SC|GN|OHC|OGN|TPB)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string DuplicateSeriesKey(string name)
    {
        var t = DuplicateSeriesYearSuffix.Replace(name.Trim(), "").Trim();
        t = DuplicateSeriesEditionParenthetical.Replace(t, "").Trim();
        // Bug #23 fix (2026-11-26): restored - Bug #22 removed this pass
        // entirely after an HC/TP pair sharing this key got wrongly
        // MERGED into one SKU by MergeDuplicateSeries. Confirmed live
        // afterward (user report, 2026-11-26): removing the strip also
        // hid every genuinely-safe HC/TP/plain trio of the SAME book
        // from ever being found or even just LISTED - a frequent shape
        // in PRH's own catalog data (the same book solicited three
        // times, worded three different ways - see this field's own
        // doc comment above for real examples). This key's only job is
        // "do these names refer to the same work?" - whether that's
        // then safe to physically MERGE is a separate question, now
        // decided by DuplicateSeriesFormatBucket below, which
        // GetDuplicateSeriesCandidateGroups applies only on
        // MergeDuplicateSeries' side, not FindDuplicateSeries'.
        t = DuplicateSeriesFormatWord.Replace(t, "").Trim();
        t = DuplicateSeriesVolumeMarker.Replace(t, "$1").Trim();
        return SeriesNameKey.Normalize(t);
    }

    // Bug #23 fix (2026-11-26): added alongside DuplicateSeriesFormatWord's
    // return above. Categorizes a series' raw (un-stripped) name by which
    // physical format it names, off the same words DuplicateSeriesKey just
    // stripped out of the matching key - used ONLY by MergeDuplicateSeries
    // (via GetDuplicateSeriesCandidateGroups' caller-side filter below) to
    // decide whether a same-key, same-issue-count group is actually safe
    // to physically merge. Two names in the SAME bucket (both HC, or both
    // some trade-paperback spelling) really are the identical SKU worded
    // two ways - safe to merge. Two names in DIFFERENT buckets (HC vs TP,
    // or either vs a plain/no-format name) are the dangerous Bug #22
    // shape - two or three real, separately-priced/barcoded products that
    // must never collapse into one Issue/Cover row - but still worth
    // surfacing in FindDuplicateSeries' own scan (which calls
    // GetDuplicateSeriesCandidateGroups directly, with no bucket filter at
    // all) so the user can see them and merge by hand if they choose to.
    private static string DuplicateSeriesFormatBucket(string name)
    {
        if (Regex.IsMatch(name, @"\b(?:HC|OHC)\b", RegexOptions.IgnoreCase)) return "HC";
        if (Regex.IsMatch(name, @"\b(?:TP|SC|GN|OGN|TPB)\b", RegexOptions.IgnoreCase)) return "TRADE";
        return "PLAIN";
    }

    // Bug #23 fix (2026-11-26): the actual "possible duplicate" grouping
    // logic, pulled out into one shared helper so FindDuplicateSeries and
    // MergeDuplicateSeries can never again drift apart the way Bug #22's
    // fix had to be hand-applied to both separately. Groups by
    // DuplicateSeriesKey, then - WITHIN each key group - by issue count
    // (not across the whole key group the way Bug #22's guard worked):
    // two real printings of the identical content always collect the
    // same number of issues, so a same-key sub-group that shares one
    // issue count is a reliable "possible duplicate", independently of
    // whatever ELSE happens to share that key. Strictly more precise than
    // Bug #22's group-wide check, which excluded an entire key group -
    // including any genuinely-safe same-count members inside it - the
    // instant ANY one member had a different count (confirmed live: that
    // silently hid "DC K.O. HC" from ever pairing with "DC K.O. TP" too,
    // just because the unrelated 5-issue "DC K.O." floppy run happened to
    // share their key).
    private static List<(string Key, List<Series> Members)> GetDuplicateSeriesCandidateGroups(List<Series> allSeries)
    {
        return allSeries
            .GroupBy(s => DuplicateSeriesKey(s.Name), StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g
                .GroupBy(s => s.Issues.Count)
                .Where(sub => sub.Count() > 1)
                .Select(sub => (Key: g.Key, Members: sub.ToList())))
            .ToList();
    }

    // Read-only - added 2026-11-24 alongside the LunarTitleParser fix
    // above, specifically so the duplicate series that bug already
    // created (before the fix landed) can be reviewed BEFORE anything
    // merges them - see MergeDuplicateSeries just below for the action
    // this is meant to be checked against first. Makes no database
    // changes at all.
    public AutoFetchResult FindDuplicateSeries()
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Duplicate-series scan at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        var allSeries = _db.AllSeries.Include(s => s.Issues).ToList();
        // Bug #23 fix (2026-11-26): now the shared helper (see its own
        // doc comment above). This scan applies no format-bucket
        // restriction at all, unlike MergeDuplicateSeries below -
        // specifically so a genuine HC-vs-TP(-vs-plain) trio of the same
        // book still shows up HERE for the user to see and act on by
        // hand, even though the merge action itself won't touch it
        // automatically.
        var groups = GetDuplicateSeriesCandidateGroups(allSeries);

        if (groups.Count == 0)
        {
            trace.AppendLine("No duplicate series found.");
        }
        else
        {
            foreach (var group in groups)
            {
                trace.AppendLine();
                trace.AppendLine($"Possible duplicate: \"{group.Key}\"");
                foreach (var s in group.Members.OrderByDescending(s => s.Issues.Count))
                {
                    var line = $"  Series #{s.Id}: \"{s.Name}\" - {s.Issues.Count} issue(s), StartYear {s.StartYear}"
                        + (s.ComicVineVolumeId != null ? $", Comic Vine volume {s.ComicVineVolumeId}" : "")
                        + (s.MetronSeriesId != null ? $", Metron series {s.MetronSeriesId}" : "");
                    result.Added.Add(line);
                    trace.AppendLine(line);
                }
            }
        }

        trace.AppendLine();
        trace.AppendLine($"Found {groups.Count} possible duplicate group(s). This is a dry run - no changes were made.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        return result;
    }

    // Actually performs the merge for every group FindDuplicateSeries
    // above would report - run this only after reviewing that dry run's
    // output, since this one really changes the database (not reversible
    // from inside the app - back up comic_pos.db first if in doubt).
    //
    // Picks the "keeper" in each group as whichever member has the most
    // issues already, tie-broken by whichever is linked to a real
    // external identity (Comic Vine volume, then Metron series id), then
    // by lowest Id (the oldest/most-established row) - a brand-new,
    // mostly-empty series created by tonight's parser bug should never
    // outrank an already-established one just by chance ordering.
    //
    // For every OTHER series in a group: an issue whose number doesn't
    // already exist on the keeper is simply re-parented onto it (cheap -
    // just a SeriesId reassignment). An issue number that DOES already
    // exist on the keeper (a genuine overlap, not just a name-level
    // split) gets its covers merged in instead, in three tiers (updated
    // 2026-11-24 after a real run on "Black Tower: The Raven Conspiracy"
    // #4 showed the original two-tier version leaving a stale, never-
    // upgraded placeholder cover sitting right next to the duplicate's
    // real one): (1) a cover whose Barcode already exists on the keeper's
    // copy is dropped as a true duplicate (Cover.Barcode has a DB-level
    // unique index - see ComicDbContext - so two rows could never
    // legitimately share one anyway); (2) a cover whose LETTER matches one
    // already on the keeper's issue, where the keeper's own cover still
    // looks like a placeholder (LooksLikePlaceholderCover - same check
    // Bug #7's AddMissingCoverVariants fix uses), gets its real data
    // copied onto that EXISTING keeper cover in place (keeping the
    // keeper's own Cover row/Id, in case a sale line or hold already
    // references it) rather than being kept as a separate, re-lettered
    // row; (3) everything else gets a free cover letter via the same
    // ResolveCoverLetters normal variant-cover discovery already uses.
    // The now-emptied duplicate issue itself is deleted once its covers
    // have all been consumed one of these three ways. Any PullListEntry
    // pointing at a series being removed is re-pointed at the keeper
    // first, so no subscriber's pull list silently breaks.
    public AutoFetchResult MergeDuplicateSeries(Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Duplicate-series merge run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        var allSeries = _db.AllSeries.Include(s => s.Issues).ThenInclude(i => i.Covers).ToList();
        // Bug #23 fix (2026-11-26): now the shared helper (see its own
        // doc comment above, by DuplicateSeriesKey/DuplicateSeriesFormatBucket).
        var candidateGroups = GetDuplicateSeriesCandidateGroups(allSeries);

        // Bug #23 fix (2026-11-26): a candidate group is only safe to
        // actually MERGE when every member names the same physical format
        // (DuplicateSeriesFormatBucket) - see that method's own doc
        // comment for the full reasoning. A mixed-bucket group (an HC
        // paired with a TP, or either paired with a plain/no-format name)
        // is still shown below as FOUND, but deliberately left alone:
        // these are two or three genuinely different, separately-priced/
        // barcoded products (Bug #22's whole point), not one product
        // worded two ways.
        var groups = candidateGroups
            .Where(g => g.Members.Select(s => DuplicateSeriesFormatBucket(s.Name)).Distinct().Count() == 1)
            .ToList();
        var skipped = candidateGroups
            .Where(g => g.Members.Select(s => DuplicateSeriesFormatBucket(s.Name)).Distinct().Count() != 1)
            .ToList();

        foreach (var g in skipped)
        {
            trace.AppendLine();
            trace.AppendLine($"Found \"{g.Key}\" ({g.Members.Count} series, same issue count) but NOT merging - different formats (HC/TP/plain), needs a manual look:");
            foreach (var s in g.Members.OrderByDescending(s => s.Issues.Count))
                trace.AppendLine($"  Series #{s.Id}: \"{s.Name}\" - {s.Issues.Count} issue(s)");
        }

        if (groups.Count == 0)
        {
            trace.AppendLine();
            trace.AppendLine(skipped.Count == 0
                ? "No duplicate series found - nothing to merge."
                : $"No same-format duplicate series found to merge ({skipped.Count} different-format group(s) above need a manual look).");
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            onProgress?.Invoke(skipped.Count == 0
                ? "No duplicates found - nothing to merge."
                : $"No same-format duplicates to merge - {skipped.Count} different-format group(s) need a manual look (see log).");
            return result;
        }

        int seriesRemoved = 0, issuesMoved = 0, issuesMerged = 0, coversDropped = 0;

        foreach (var group in groups)
        {
            var members = group.Members
                .OrderByDescending(s => s.Issues.Count)
                .ThenByDescending(s => s.ComicVineVolumeId.HasValue)
                .ThenByDescending(s => s.MetronSeriesId.HasValue)
                .ThenBy(s => s.Id)
                .ToList();

            var keeper = members[0];
            onProgress?.Invoke($"Merging \"{group.Key}\" - keeping Series #{keeper.Id} (\"{keeper.Name}\", {keeper.Issues.Count} issue(s))...");
            trace.AppendLine();
            trace.AppendLine($"\"{group.Key}\": keeping Series #{keeper.Id} (\"{keeper.Name}\")");

            foreach (var dup in members.Skip(1))
            {
                trace.AppendLine($"  merging Series #{dup.Id} (\"{dup.Name}\", {dup.Issues.Count} issue(s)) into it...");

                foreach (var issue in dup.Issues.ToList())
                {
                    // Bug #19 fix (2026-11-26): matching by IssueNumber
                    // ALONE here was wrong whenever the widened
                    // DuplicateSeriesKey (Bug #18) pairs an ongoing/
                    // limited series (its own real issue #1, #2, ... as
                    // IssueType.SingleIssue) with its OWN later collected
                    // edition (always IssueType.CollectedEdition,
                    // IssueNumber = 1 - see PrhTitleParser's own doc
                    // comment) - confirmed live via a real "Find
                    // Duplicate Series" scan surfacing 19 such groups
                    // (e.g. "Clayface: Celebrity Dirt" - 6 real single
                    // issues - grouped with "CLAYFACE CELEBRITY DIRT TP"
                    // - 1 collected edition). Without the Type check, the
                    // TP's real barcode/price would have been merged in
                    // as just another COVER LETTER of the ongoing run's
                    // real single issue #1 - filing a $19.99 trade as "an
                    // alternate cover of the $3.99 single issue," which
                    // is wrong, not just untidy (Checkout/Inventory would
                    // show the trade's price/barcode nested under the
                    // single issue instead of as its own catalog entry).
                    // No unique index forces one Issue row per
                    // (SeriesId, IssueNumber) - confirmed via Issue.cs -
                    // so a same-series, same-number, different-Type pair
                    // can coexist as two separate Issue rows with no
                    // conflict; falling through to the "no conflict, just
                    // move it" branch below is the correct outcome, not a
                    // workaround.
                    var existingIssue = keeper.Issues.FirstOrDefault(i => i.IssueNumber == issue.IssueNumber && i.Type == issue.Type);
                    if (existingIssue == null)
                    {
                        issue.SeriesId = keeper.Id;
                        keeper.Issues.Add(issue);
                        issuesMoved++;
                        trace.AppendLine($"    issue #{issue.IssueNumber:0.##}: moved onto Series #{keeper.Id} (no conflict).");
                    }
                    else
                    {
                        var existingLetters = existingIssue.Covers.Select(c => c.CoverLetter).ToList();
                        var claimedBarcodes = existingIssue.Covers.Select(c => c.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var candidates = new List<(Cover Cover, bool IsGuess)>();
                        var upgradedCovers = 0;
                        foreach (var cover in issue.Covers.ToList())
                        {
                            if (claimedBarcodes.Contains(cover.Barcode))
                            {
                                _db.Covers.Remove(cover);
                                coversDropped++;
                                trace.AppendLine($"    issue #{issue.IssueNumber:0.##}: dropped duplicate cover (barcode {cover.Barcode} already on Series #{keeper.Id}'s copy).");
                                continue;
                            }

                            // Added 2026-11-24 after a real merge run (Black
                            // Tower: The Raven Conspiracy #4) showed exactly
                            // the gap this closes: the keeper's own Cover A
                            // was still a $0/placeholder row (its Lunar row
                            // had landed under the DUPLICATE series instead,
                            // back before Bug #6's SeriesNameKey widening
                            // fix existed), and the old code below would
                            // have just bolted the duplicate's real Cover A
                            // on as a brand-new, re-lettered variant via
                            // ResolveCoverLetters - leaving the original
                            // placeholder sitting there untouched, wrong,
                            // and now confusingly duplicated. Same
                            // placeholder-upgrade check AddMissingCoverVariants
                            // uses for Bug #7, applied here too: a cover on
                            // the keeper that shares this one's LETTER and
                            // still looks like a placeholder gets its real
                            // data copied in (keeping the keeper's own Cover
                            // row/Id, in case anything - a sale line, a hold -
                            // already references it), instead of the
                            // duplicate's richer cover being kept as a
                            // second, separately-lettered row next to a
                            // stale placeholder that never gets cleaned up.
                            var matchByLetter = existingIssue.Covers.FirstOrDefault(c =>
                                string.Equals(c.CoverLetter, cover.CoverLetter, StringComparison.OrdinalIgnoreCase));
                            if (matchByLetter != null && LooksLikePlaceholderCover(matchByLetter))
                            {
                                matchByLetter.Barcode = cover.Barcode;
                                matchByLetter.Price = cover.Price;
                                if (string.IsNullOrWhiteSpace(matchByLetter.Description)) matchByLetter.Description = cover.Description;
                                if (string.IsNullOrWhiteSpace(matchByLetter.CoverArtist)) matchByLetter.CoverArtist = cover.CoverArtist;
                                if (string.IsNullOrWhiteSpace(matchByLetter.CoverImagePath)) matchByLetter.CoverImagePath = cover.CoverImagePath;
                                claimedBarcodes.Add(cover.Barcode);
                                issue.Covers.Remove(cover);
                                _db.Covers.Remove(cover);
                                upgradedCovers++;
                                trace.AppendLine($"    issue #{issue.IssueNumber:0.##}: upgraded Series #{keeper.Id}'s placeholder Cover {matchByLetter.CoverLetter} with the duplicate's real price/UPC.");
                                continue;
                            }

                            cover.IssueId = existingIssue.Id;
                            // Explicit detach from the old issue's
                            // navigation collection rather than relying on
                            // EF's change-tracker fixup to notice the FK
                            // moved before _db.Issues.Remove(issue) runs
                            // below - issue.Covers is a required
                            // relationship, and a cover still sitting in
                            // that collection in memory when its parent
                            // gets removed is exactly the kind of ambiguity
                            // that risks cascade-deleting a cover that was
                            // actually just supposed to move.
                            issue.Covers.Remove(cover);
                            candidates.Add((cover, true));
                            claimedBarcodes.Add(cover.Barcode);
                        }
                        if (candidates.Count > 0)
                        {
                            ResolveCoverLetters(candidates, existingLetters);
                            foreach (var (cover, _) in candidates) existingIssue.Covers.Add(cover);
                            trace.AppendLine($"    issue #{issue.IssueNumber:0.##}: merged {candidates.Count} cover(s) onto Series #{keeper.Id}'s existing issue.");
                        }
                        if (upgradedCovers > 0)
                            trace.AppendLine($"    issue #{issue.IssueNumber:0.##}: {upgradedCovers} placeholder cover(s) upgraded in place.");
                        _db.Issues.Remove(issue);
                        issuesMerged++;
                    }
                }

                var pullListEntries = _db.PullListEntries.Where(p => p.SeriesId == dup.Id).ToList();
                foreach (var p in pullListEntries) p.SeriesId = keeper.Id;
                if (pullListEntries.Count > 0)
                    trace.AppendLine($"    re-pointed {pullListEntries.Count} pull-list entr{(pullListEntries.Count == 1 ? "y" : "ies")} from Series #{dup.Id} to #{keeper.Id}.");

                _db.AllSeries.Remove(dup);
                seriesRemoved++;
            }
        }

        _db.SaveChanges();

        trace.AppendLine();
        trace.AppendLine($"Done. {groups.Count} duplicate group(s) merged, {seriesRemoved} empty series removed, {issuesMoved} issue(s) moved outright, {issuesMerged} issue(s) merged (covers combined), {coversDropped} exact-duplicate cover(s) dropped{(skipped.Count > 0 ? $"; {skipped.Count} different-format group(s) left alone (see above)" : "")}.");
        result.Added.Add($"Merged {groups.Count} duplicate series group(s) - {seriesRemoved} empty series removed.{(skipped.Count > 0 ? $" {skipped.Count} different-format group(s) found but not merged (see trace)." : "")}");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke($"Done - merged {groups.Count} duplicate group(s).{(skipped.Count > 0 ? $" {skipped.Count} different-format group(s) left for a manual look." : "")}");
        return result;
    }

    // Hand-picked one-time cleanup, added 2026-11-24 - hardcoded to the
    // exact two duplicate series FindDuplicateSeries turned up on the
    // user's own real data that night (Series #1972 "DARKNESS (2025)"
    // and #1973 "ZATANNA (2026)", both brand-new single-issue rows the
    // Lunar parser bug created), not a general-purpose "delete any
    // series" feature - same spirit as RetryComicadeEnrichmentForSpecificIssues
    // above. Deliberately a plain delete rather than MergeDuplicateSeries:
    // the user asked for this specifically to avoid MergeDuplicateSeries'
    // cover-merging touching the already-correct established issue at
    // all (its Publisher/Description/etc. stay completely untouched this
    // way, instead of gaining a second, oddly-lettered cover). The
    // established series (#1706 "The Darkness", #1761 "Zatanna") are
    // never modified - only the two new rows are removed, after
    // re-pointing any PullListEntry that might already point at one
    // (unlikely for a series created hours earlier, but cheap to check).
    public AutoFetchResult DeleteKnownDuplicateSeries(Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Known-duplicate delete run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        // (duplicate series id, established/keeper series id to re-point
        // any pull-list entry to instead of just deleting it)
        var targets = new (int DupId, int KeeperId)[]
        {
            (1972, 1706), // "DARKNESS (2025)" -> "The Darkness"
            (1973, 1761), // "ZATANNA (2026)" -> "Zatanna"
        };

        foreach (var (dupId, keeperId) in targets)
        {
            var dup = _db.AllSeries.Include(s => s.Issues).ThenInclude(i => i.Covers)
                .FirstOrDefault(s => s.Id == dupId);
            if (dup == null)
            {
                var line = $"Series #{dupId}: not found (already removed?) - skipped.";
                result.Errors.Add(line);
                trace.AppendLine(line);
                continue;
            }

            onProgress?.Invoke($"Deleting Series #{dup.Id} (\"{dup.Name}\")...");

            var pullListEntries = _db.PullListEntries.Where(p => p.SeriesId == dupId).ToList();
            foreach (var p in pullListEntries) p.SeriesId = keeperId;
            if (pullListEntries.Count > 0)
                trace.AppendLine($"  re-pointed {pullListEntries.Count} pull-list entr{(pullListEntries.Count == 1 ? "y" : "ies")} from Series #{dupId} to #{keeperId} instead of deleting them.");

            foreach (var issue in dup.Issues.ToList())
            {
                foreach (var cover in issue.Covers.ToList())
                    _db.Covers.Remove(cover);
                _db.Issues.Remove(issue);
            }
            _db.AllSeries.Remove(dup);

            var removedLine = $"Deleted Series #{dup.Id} (\"{dup.Name}\") - {dup.Issues.Count} issue(s), {dup.Issues.Sum(i => i.Covers.Count)} cover(s). Series #{keeperId} was left untouched.";
            result.Added.Add(removedLine);
            trace.AppendLine(removedLine);
        }

        _db.SaveChanges();

        trace.AppendLine();
        trace.AppendLine($"Done. {result.Added.Count} series deleted.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke($"Done - {result.Added.Count} series deleted.");
        return result;
    }

    // One-time cleanup, added 2026-11-24 right alongside the
    // AddMissingCoverVariants fix (see its own doc comment) - the store
    // asked to wipe any Lunar book that never actually got real data so a
    // fresh re-import (now that the enrichment bug is fixed) starts
    // clean, rather than leaving broken rows around for the fix to find
    // later on its own.
    //
    // Deliberately narrower than "every book Lunar ever touched": there is
    // no column anywhere that records which source added a given Cover
    // (Barcode is real inventory data, not a provenance tag), so a cover
    // that Lunar imported WITH a real UPC is completely indistinguishable
    // from one Metron or LOCG found first - and, now that
    // AddMissingCoverVariants is fixed, that data is correctly saved and
    // there is no reason to throw it away. The only Lunar rows that are
    // actually broken, and the only ones this can safely identify, are
    // the ones that never got a real UPC at all - LunarDistributionParser
    // falls back to a generated "LUN-{ProductCode}" barcode for those (see
    // its own Parse method), which is a safe, unambiguous marker since no
    // other source ever generates a barcode starting with "LUN-". This
    // deletes exactly those placeholder covers, then cleans up any issue
    // or series that's left with nothing on it as a result - mirroring
    // DeleteKnownDuplicateSeries's cascade-delete shape above.
    public AutoFetchResult DeleteLunarPlaceholderCovers(Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Lunar placeholder-cover delete run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        onProgress?.Invoke("Scanning for Lunar covers that never got a real UPC...");

        var placeholders = _db.Covers
            .Include(c => c.Issue).ThenInclude(i => i.Series)
            .Where(c => c.Barcode.StartsWith("LUN-"))
            .ToList();

        if (placeholders.Count == 0)
        {
            var noneLine = "Nothing to delete - no Lunar placeholder covers found.";
            trace.AppendLine(noneLine);
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            onProgress?.Invoke(noneLine);
            return result;
        }

        var coversDeleted = 0;
        var issuesDeleted = 0;
        var touchedSeriesIds = new HashSet<int>();

        foreach (var issueGroup in placeholders.GroupBy(c => c.Issue))
        {
            var issue = issueGroup.Key;
            var series = issue.Series;
            var toRemove = issueGroup.ToList();

            foreach (var cover in toRemove)
            {
                issue.Covers.Remove(cover);
                _db.Covers.Remove(cover);
            }
            coversDeleted += toRemove.Count;

            if (issue.Covers.Count == 0)
            {
                var line = $"\"{series.Name}\" #{issue.IssueNumber:0.##}: only had placeholder cover(s) - deleting the issue too.";
                trace.AppendLine(line);
                onProgress?.Invoke(line);
                touchedSeriesIds.Add(issue.SeriesId);
                _db.Issues.Remove(issue);
                issuesDeleted++;
            }
            else
            {
                var line = $"\"{series.Name}\" #{issue.IssueNumber:0.##}: removed {toRemove.Count} placeholder cover(s), kept {issue.Covers.Count} real one(s).";
                trace.AppendLine(line);
                onProgress?.Invoke(line);
            }
        }

        var (saved, saveError) = TrySaveChanges();
        if (!saved)
        {
            var failLine = $"FAILED TO SAVE: {saveError}";
            result.Errors.Add(failLine);
            trace.AppendLine(failLine);
            result.Trace = trace.ToString();
            DebugLog.WriteCheckNowLog(result.Trace);
            onProgress?.Invoke(failLine);
            return result;
        }

        // A series can only have lost issues above, never gained one, so
        // re-checking just the series this run actually touched (rather
        // than every series in the store) is enough to catch any that are
        // now completely empty.
        var seriesDeleted = 0;
        foreach (var seriesId in touchedSeriesIds)
        {
            var series = _db.AllSeries.Include(s => s.Issues).FirstOrDefault(s => s.Id == seriesId);
            if (series != null && series.Issues.Count == 0)
            {
                var line = $"\"{series.Name}\" has no issues left - deleting the series too.";
                trace.AppendLine(line);
                onProgress?.Invoke(line);
                _db.AllSeries.Remove(series);
                seriesDeleted++;
            }
        }
        if (seriesDeleted > 0)
        {
            var (saved2, saveError2) = TrySaveChanges();
            if (!saved2)
            {
                var failLine = $"Series cleanup FAILED TO SAVE: {saveError2}";
                result.Errors.Add(failLine);
                trace.AppendLine(failLine);
                onProgress?.Invoke(failLine);
            }
        }

        var summary = $"Deleted {coversDeleted} placeholder cover(s), {issuesDeleted} now-empty issue(s), {seriesDeleted} now-empty series.";
        result.Added.Add(summary);
        trace.AppendLine();
        trace.AppendLine($"Done. {summary}");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke($"Done - {summary}");
        return result;
    }

    // Dev/demo seed data - added 2026-11-24, the store asked for fake
    // customers and a fake sales history built FROM the real inventory
    // that actually came in through the recent Lunar imports, so the
    // Dashboard/History/Customers pages have something realistic to show
    // while the rest of the app is still being built out, instead of
    // sitting empty.
    //
    // "Built from the real Lunar imports" can't be taken completely
    // literally: there is no column anywhere that records which source
    // added a given Cover (see DeleteLunarPlaceholderCovers's own doc
    // comment for the same limitation), so a Lunar-imported cover with a
    // real UPC is indistinguishable from one Metron or LOCG found first.
    // What this uses instead is every REAL (non-placeholder) cover
    // currently in inventory - which, given how much of the catalog the
    // Lunar backfill just added, is overwhelmingly Lunar's own real,
    // successfully-imported data anyway. Placeholder covers (see
    // LooksLikePlaceholderCover) are skipped so a fake sale never cites a
    // $0 price or a generated barcode as if it were a real past purchase.
    //
    // Purely additive and safe to click more than once: existing fake (or
    // real) customers whose name already matches one of the pool below
    // are left alone rather than duplicated, but each run still adds a
    // fresh batch of backdated Sales (and tops up anyone under-subscribed
    // on fake pull-list entries - see below) on top of whatever's already
    // there - that's the point of an "add some fake info" button, not a
    // one-shot seeder. Real inventory stock is decremented for each fake
    // sale, the same way CheckoutService.CompleteSale does for a real
    // one - though unlike a real sale, this never lets stock go negative
    // (see the stock-bump comment inline below) - so the Inventory/New
    // Books screens stay internally consistent with the fake history
    // this generates instead of showing full stock next to a customer
    // who "already bought" the last copy, or a negative count on
    // something that was never actually received.
    private static readonly string[] FakeFirstNames =
    {
        "Alex", "Jordan", "Taylor", "Morgan", "Casey", "Riley", "Jamie", "Avery",
        "Sam", "Drew", "Quinn", "Reese", "Harper", "Rowan", "Skyler", "Emerson",
        "Kai", "Dakota", "Finley", "Peyton"
    };
    private static readonly string[] FakeLastNames =
    {
        "Nguyen", "Garcia", "Smith", "Patel", "Kim", "Johnson", "Martinez", "Lee",
        "Brown", "Ortiz", "Clark", "Singh", "Wright", "Hughes", "Flores", "Bennett",
        "Reyes", "Foster", "Hayes", "Sullivan"
    };

    public AutoFetchResult GenerateFakeCustomersAndSalesHistory(int customerCount = 15, int saleCount = 60, Action<string>? onProgress = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Fake seed-data run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        trace.AppendLine(new string('=', 60));

        var pool = _db.Covers
            .Include(c => c.Issue).ThenInclude(i => i.Series)
            .Where(c => c.Price > 0)
            .ToList()
            .Where(c => !LooksLikePlaceholderCover(c))
            .ToList();

        if (pool.Count == 0)
        {
            var noneLine = "Nothing to seed from - no real (non-placeholder) covers found in inventory yet. Import some books first.";
            trace.AppendLine(noneLine);
            result.Errors.Add(noneLine);
            result.Trace = trace.ToString();
            onProgress?.Invoke(noneLine);
            return result;
        }

        var rng = new Random();

        onProgress?.Invoke($"Found {pool.Count} real cover(s) to draw fake sales from. Creating fake customers...");

        var existingNames = _db.Customers.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customers = new List<Customer>();
        var attempts = 0;
        while (customers.Count < customerCount && attempts < customerCount * 4)
        {
            attempts++;
            var name = $"{FakeFirstNames[rng.Next(FakeFirstNames.Length)]} {FakeLastNames[rng.Next(FakeLastNames.Length)]}";
            if (!existingNames.Add(name)) continue; // already exists (or already picked this run) - skip, don't duplicate

            var email = $"{name.Replace(" ", ".").ToLowerInvariant()}@example.com";
            var phone = $"555-{rng.Next(1000, 9999)}";
            var customer = _customers.AddCustomer(name, email, phone);
            customers.Add(customer);
        }
        var newCustomerLine = $"Added {customers.Count} new fake customer(s).";
        trace.AppendLine(newCustomerLine);
        onProgress?.Invoke(newCustomerLine);

        // Also sell to any customer already on file (real or from an
        // earlier run of this same button) - otherwise re-running this
        // would only ever grow the customer list for one visit each and
        // never give anyone a repeat-purchase history.
        var allCustomers = _db.Customers.Where(c => c.IsActive).ToList();
        if (allCustomers.Count == 0)
        {
            var noCustomersLine = "No active customers to sell to (even after adding fake ones) - nothing to do.";
            trace.AppendLine(noCustomersLine);
            result.Errors.Add(noCustomersLine);
            result.Trace = trace.ToString();
            onProgress?.Invoke(noCustomersLine);
            return result;
        }

        var salesCreated = 0;
        var linesCreated = 0;
        var receivedCovers = new HashSet<int>();
        for (var i = 0; i < saleCount; i++)
        {
            var customer = allCustomers[rng.Next(allCustomers.Count)];
            // Spread across the last ~120 days so History/Dashboard charts
            // show a real-looking spread instead of every fake sale
            // bunched on today's date.
            var soldAt = DateTime.Now.AddDays(-rng.Next(0, 120)).AddHours(-rng.Next(0, 10));

            var sale = new Sale { CustomerId = customer.Id, SoldAt = soldAt };
            var lineItemCount = rng.Next(1, 4);
            for (var j = 0; j < lineItemCount; j++)
            {
                var cover = pool[rng.Next(pool.Count)];
                if (sale.Lines.Any(l => l.CoverId == cover.Id)) continue; // don't double up the same cover within one fake sale

                sale.Lines.Add(new SaleLine
                {
                    Cover = cover,
                    CoverId = cover.Id,
                    Quantity = 1,
                    PriceEach = cover.Price,
                    // A small fraction look like a loyalty/member discount
                    // rather than every single fake sale selling at exact
                    // sticker price.
                    DiscountPercent = rng.Next(0, 10) == 0 ? 10 : 0,
                });

                // Changed 2026-11-24: used to mirror CheckoutService.
                // CompleteSale's own stock deduction exactly, deliberately
                // letting it go negative (an "honest oversell signal"). A
                // real run showed why that's wrong for SEED data
                // specifically: the pool here draws from every real,
                // non-placeholder cover in the whole catalog, which after
                // the Lunar backfill is overwhelmingly stuff that was only
                // ever DISCOVERED (Metron/Lunar enrichment can fill in a
                // real price/UPC long before anyone physically receives a
                // copy - see Bug #7/#8) and still sits at 0 stock. Selling
                // a fake sale against one of those isn't a believable
                // "oversold" edge case, it's nonsense (selling something
                // the store never stocked) - and now that Inventory only
                // shows items with StockQuantity > 0 (user feedback: "i
                // dont think anything should be in the inventory until we
                // add it ourself" - see IInventoryService.
                // SearchReceivedSeriesForDisplay), a negative count would
                // make an un-received book show up in Inventory looking
                // actively broken. So: if this cover has never been
                // "received" (stock already at or below 0), simulate the
                // store stocking a small, believable handful of copies
                // first, THEN sell one - same spirit as a real receiving
                // workflow (Add New Item), just invented here since this
                // is fake history. Never goes negative from here on.
                if (cover.StockQuantity <= 0)
                {
                    cover.StockQuantity += rng.Next(2, 7);
                    receivedCovers.Add(cover.Id);
                }
                cover.StockQuantity -= 1;
            }

            if (sale.Lines.Count == 0) continue; // every pick this round collided - vanishingly rare, just skip this one

            _db.Sales.Add(sale);
            salesCreated++;
            linesCreated += sale.Lines.Count;
        }

        // Fake pull-list subscriptions, added 2026-11-24 ("add more fake
        // subscribers series books to the list") - the fake sales above
        // give Dashboard/History/Customers something to look at, but
        // nobody had any ongoing pull-list subscriptions, which left the
        // FOC Report and "Who's Pulling This?" screens empty even with a
        // full fake customer roster. Every series actually represented in
        // `pool` (real, non-placeholder covers already in inventory, same
        // set the fake sales above draw from) is fair game - 1-3 distinct
        // series per customer, skipping any (customer, series) pair that
        // already has an active subscription so a second run only tops
        // people up instead of duplicating rows (PullListEntry has no
        // unique index of its own for this, so that check is done here by
        // hand, same idea as PullListService.AddToPullList's own
        // idempotency). Always defaults to Cover A rather than trying to
        // read back each series' real known letters - simple, and "A" is
        // what the overwhelming majority of pull subscriptions actually
        // want anyway (see AddToPullListModal's own default).
        var seriesPool = pool.Select(c => c.Issue.SeriesId).Distinct().ToList();
        var existingSubscriptions = _db.PullListEntries
            .Where(p => p.IsActive)
            .Select(p => new { p.CustomerId, p.SeriesId })
            .ToHashSet();
        var subscriptionsCreated = 0;
        if (seriesPool.Count > 0)
        {
            foreach (var customer in allCustomers)
            {
                var subscribeCount = rng.Next(1, 4); // 1-3 series per customer
                var pickedThisCustomer = new HashSet<int>();
                var ceiling = subscribeCount * 4; // bounded attempts - don't spin forever once a customer is nearly fully subscribed
                for (var attempt = 0; attempt < ceiling && pickedThisCustomer.Count < subscribeCount; attempt++)
                {
                    var seriesId = seriesPool[rng.Next(seriesPool.Count)];
                    if (!pickedThisCustomer.Add(seriesId)) continue;
                    if (existingSubscriptions.Contains(new { CustomerId = customer.Id, SeriesId = seriesId })) continue;

                    _db.PullListEntries.Add(new PullListEntry
                    {
                        CustomerId = customer.Id,
                        SeriesId = seriesId,
                        PreferredCoverLetter = "A",
                        Quantity = 1,
                        IsActive = true
                    });
                    existingSubscriptions.Add(new { CustomerId = customer.Id, SeriesId = seriesId });
                    subscriptionsCreated++;
                }
            }
        }

        var (saved, saveError) = TrySaveChanges();
        if (!saved)
        {
            var failLine = $"FAILED TO SAVE: {saveError}";
            result.Errors.Add(failLine);
            trace.AppendLine(failLine);
            result.Trace = trace.ToString();
            onProgress?.Invoke(failLine);
            return result;
        }

        var summary = $"Added {customers.Count} fake customer(s), {salesCreated} fake sale(s) ({linesCreated} line item(s) total), and {subscriptionsCreated} fake pull-list subscription(s), drawn from {pool.Count} real cover(s) already in inventory."
            + (receivedCovers.Count > 0 ? $" Simulated receiving stock for {receivedCovers.Count} previously-unstocked cover(s) so those fake sales had something real to sell." : "");
        result.Added.Add(summary);
        trace.AppendLine(summary);
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);
        onProgress?.Invoke($"Done - {summary}");
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

    // Scans every issue for a FocDate predating year 2000 - impossible
    // for any real solicitation anywhere in this store's history (the
    // earliest genuine PRH/Lunar date in this whole app is 2021) - and
    // corrects it to the same "3 weeks before street date" estimate
    // already used when a Comic-Vine-sourced issue has no real FOC date
    // at all (see RunCheckForNewIssues' own
    // FocDate = cvIssue.StoreDate?.AddDays(-21)). Added 2026-11-26 after
    // 38 "Absolute Universe" issues (all Lunar-sourced) turned up with
    // FocDate 1900-01-01/1900-11-01 - traced to LunarDistributionParser.
    // GetDate trying ClosedXML's typed DateTime cell accessor before ever
    // falling back to text, so a blank or stray non-date "FinalOrderCutoff"
    // cell could still "succeed" with a bogus date near Excel's own 1900
    // epoch instead of failing cleanly (see that parser's own doc comment
    // for the fix on the import side - this is only the catch-up for
    // whatever already got saved that way before today). Purely local, no
    // network calls - safe to always run here alongside
    // ClearPlaceholderCovers above.
    private int FixImplausibleFocDates()
    {
        var bad = _db.Issues
            .Where(i => i.FocDate != null && i.FocDate.Value.Year < 2000)
            .ToList();

        var fixedCount = 0;
        foreach (var issue in bad)
        {
            // Release date is always the better-trusted of the two here -
            // every one of the 38 that surfaced this had a perfectly
            // normal ReleaseDate and only FocDate was broken. Falling back
            // to null (rather than leaving the provably-wrong 1900 value)
            // only matters for the rare case where ReleaseDate is somehow
            // ALSO missing - unknown is safer than wrong.
            issue.FocDate = issue.ReleaseDate.HasValue
                ? issue.ReleaseDate.Value.AddDays(-21)
                : null;
            fixedCount++;
        }

        if (fixedCount > 0) _db.SaveChanges();
        return fixedCount;
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

        // Bug #10 fix (2026-11-24): claimed across the WHOLE batch (every
        // group this one call processes), not per-group/per-issue. A real
        // backfill run still hit 'UNIQUE constraint failed: Covers.Barcode'
        // even with the per-group/per-issue guards below already live
        // (seenBarcodes's and AddMissingCoverVariants' claimedBarcodes'
        // own doc comments have the full history) - on 9/19/2026 and
        // 1/7/2026 specifically, from two DIFFERENT series/issues in the
        // same file landing on the same barcode (seen live: several
        // unrelated "BATMAN DAY 2026 - BUNDLES OF 25" promo rows, each a
        // different series/issue but all falling back to the same
        // generated LUN-{ProductCode} barcode). Neither a set scoped to
        // one group nor one scoped to one existing issue can ever catch a
        // collision between two DIFFERENT groups - each one simply
        // doesn't know the other exists. One shared set for the whole
        // batch, passed into AddMissingCoverVariants and used directly
        // below instead of a fresh set per group, closes that gap.
        //
        // Bug #12 fix (2026-11-24): still not wide enough - a real
        // "Backfill All Lunar History" run kept hitting the identical
        // 'UNIQUE constraint failed: Covers.Barcode' crash on dozens of
        // weeks, every one of them importing a FACSIMILE EDITION reprint
        // (the facsimile-title fix landed the same day - see
        // LunarTitleParser's doc comment). Root cause: a facsimile
        // reprint's real barcode/UPC had, in an earlier backfill run
        // BEFORE the facsimile fix existed, been merged straight onto the
        // ORIGINAL (non-facsimile) series/issue of the same number - the
        // parser couldn't tell them apart yet back then. Now that the
        // parser correctly resolves the facsimile to its own distinct
        // series, AddEntriesAsIssues treats it as a brand-new issue and
        // tries to insert a Cover with that same barcode - which already
        // exists, just attached to the wrong (original) issue. Seeding
        // batchClaimedBarcodes from only what THIS call's own batch
        // claims (Bug #10's fix) can never catch that, since the
        // colliding Cover was created in a COMPLETELY different, earlier
        // call. Seeding it from the WHOLE Covers table up front catches
        // this (and any other stray collision against pre-existing data)
        // before it ever reaches SaveChanges() and takes the entire
        // week's batch down with it - which is almost certainly the real
        // mechanism behind task #96's "an otherwise-successful import is
        // missing some issues" symptom (Kaya #37 present, #36 missing):
        // one bad barcode in a 300-issue weekly file crashed that WHOLE
        // week's save, silently losing every other issue in the same
        // file along with it, not just the one that collided. A
        // genuinely colliding candidate is now skipped (logged to
        // result.Errors so it's visible, not silently dropped) instead of
        // being allowed to reach SaveChanges() and blow up the batch.
        var batchClaimedBarcodes = _db.Covers.Select(c => c.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                AddMissingCoverVariants(existingIssue, group, seriesName, barcodePrefix, result, batchClaimedBarcodes);
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
            // Guards against two candidates sharing a barcode - originally
            // added 2026-11-24 scoped to just this one group, then widened
            // to the whole batch (batchClaimedBarcodes, declared above)
            // the same day once a real run showed that wasn't enough - see
            // this method's own batchClaimedBarcodes comment above for the
            // full history. A distributor's weekly catalog can legitimately
            // re-list the identical already-solicited product under more
            // than one row (seen live for trade/HC items still awaiting
            // release, and for promo bundle rows), and two genuinely
            // DIFFERENT series/issues can independently fall back to the
            // same generated placeholder barcode too - this one shared set
            // catches both, across every group this call processes.
            var coverCandidates = new List<(Cover Cover, bool IsGuess)>();
            // Tracks barcodes already claimed by something earlier in THIS
            // SAME group (i.e. a sibling cover-letter variant of the issue
            // being built right now), separate from batchClaimedBarcodes
            // (claimed DB/batch-wide, across every issue in the whole run).
            // Many publishers (confirmed: Marvel/DC-heavy data) assign the
            // SAME barcode to every cover-letter variant of one issue - a
            // UPC identifies the product (issue+price) for point-of-sale
            // scanning, not the individual print cover - but Covers.Barcode
            // has a DB-level UNIQUE index, so two Cover rows can't literally
            // store the same value. Without this distinction, every sibling
            // variant past the first collided with batchClaimedBarcodes and
            // got silently dropped here, which is why so few non-Cover-A
            // covers existed in the DB to backfill art for in the first
            // place (2026-11-25/26 fix, see claude/metron-integration-notes.md).
            var thisGroupBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in group)
            {
                foreach (var candidate in BuildCoversFromEntry(e, seriesName, issueNumber, barcodePrefix, result))
                {
                    var barcode = candidate.Cover.Barcode;
                    if (!batchClaimedBarcodes.Add(barcode))
                    {
                        if (thisGroupBarcodes.Contains(barcode))
                        {
                            // A sibling variant of this same issue legitimately
                            // shares this barcode - keep it, but it needs its
                            // own unique value for the DB's unique index. '#'
                            // never appears in a real numeric barcode, so the
                            // real barcode stays recoverable as the part
                            // before it (used by BackfillCoversFromPrhImageCdn
                            // and any future barcode/UPC lookup).
                            var derived = $"{barcode}#{candidate.Cover.CoverLetter}";
                            var suffix = 2;
                            while (!batchClaimedBarcodes.Add(derived)) { derived = $"{barcode}#{candidate.Cover.CoverLetter}{suffix}"; suffix++; }
                            candidate.Cover.Barcode = derived;
                            coverCandidates.Add(candidate);
                        }
                        else
                        {
                            // Bug #12 fix (2026-11-24): logged instead of a bare
                            // `continue` - batchClaimedBarcodes now includes every
                            // barcode already in the DB (see its own doc comment
                            // above), so a skip here can mean this literal barcode
                            // already belongs to a DIFFERENT, unrelated issue
                            // already on file - previously that went straight to
                            // SaveChanges() and took the WHOLE week's batch down
                            // with a UNIQUE-constraint crash. Visible here instead
                            // of silently vanishing either way.
                            result.Errors.Add($"{seriesName} #{issueNumber:0.##}: skipped a cover (barcode {barcode}) - already in use elsewhere in the system; needs a manual look.");
                        }
                        continue;
                    }
                    thisGroupBarcodes.Add(barcode);
                    coverCandidates.Add(candidate);
                }
            }
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
                // TypeHint (PRH's own Format column - see
                // ImportPrhCatalogRelease) overrides that name-pattern
                // guess when a source actually knows the real format
                // directly, rather than everything falling back to the
                // same guess regardless of what's actually available.
                Type = group.First().TypeHint
                    ?? (StripCollectedEditionSuffix(seriesName) != null ? IssueType.CollectedEdition : IssueType.SingleIssue),
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
        if (!string.IsNullOrWhiteSpace(e.CoverImageLocalPath))
        {
            // Lunar Distribution's own embedded cover photo - already
            // saved to disk by ImportLunarDistributionRelease before this
            // entry was built, so there's nothing to fetch here at all.
            coverImagePath = e.CoverImageLocalPath;
        }
        else if (!string.IsNullOrWhiteSpace(e.CoverImageUrl))
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
    // True for a cover that was created from a cheap list-only call with
    // no real data yet, rather than one that's genuinely been enriched -
    // the SAME check RetryMetronEnrichment/RetryLocgEnrichment already use
    // to find their own candidates ($0 price + a "MTR-"/"LOCG-" barcode -
    // see their own query comments). Reused here, not reinvented, so
    // "placeholder" means the same thing everywhere in this file. "LUN-"
    // added 2026-11-24 alongside this method's first use (AddMissingCoverVariants
    // below) - a Lunar row with no real UPC column value gets this same
    // generated-fallback barcode (see LunarDistributionParser.Parse), so
    // it deserves the same later-upgrade treatment as an MTR-/LOCG- one.
    private static bool LooksLikePlaceholderCover(Cover cover) =>
        cover.Price == 0 && (cover.Barcode.StartsWith("MTR-", StringComparison.OrdinalIgnoreCase)
            || cover.Barcode.StartsWith("LOCG-", StringComparison.OrdinalIgnoreCase)
            || cover.Barcode.StartsWith("LUN-", StringComparison.OrdinalIgnoreCase));

    // claimedBarcodes is shared across the WHOLE batch this call is part
    // of (see Bug #10's fix, AddEntriesAsIssues' own doc comment on
    // batchClaimedBarcodes, for the full story) - every call site passes
    // in one set it declared once before its own loop, not a fresh one
    // per issue, so a collision between THIS issue's covers and some
    // OTHER issue's covers processed earlier in the same run is caught
    // too, not just a collision within this one issue's own group.
    private void AddMissingCoverVariants(Issue existingIssue, IEnumerable<ComicListEntry> group, string seriesName, string barcodePrefix, AutoFetchResult result, HashSet<string> claimedBarcodes)
    {
        var existingLetters = existingIssue.Covers
            .Select(c => c.CoverLetter)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Was a fresh snapshot of just THIS issue's own existing
        // barcodes, built here on every call - added 2026-11-24 after a
        // real Lunar backfill run hit a 'UNIQUE constraint failed:
        // Covers.Barcode' save failure on several real weeks (358/347/155
        // /9 entries each, all lost together since TrySaveChanges rolls
        // back the whole date on any failure). That fixed collisions
        // WITHIN one issue's group, but a later run (2026-11-24, same
        // day) still hit the identical crash on 9/19/2026 and 1/7/2026 -
        // this time from two DIFFERENT issues in the same batch landing
        // on the same barcode (seen live: several unrelated "BATMAN DAY
        // 2026 - BUNDLES OF 25" promo rows, each a different series/issue
        // but all falling back to the same generated LUN-{ProductCode}
        // barcode). A per-issue set can never catch that, no matter how
        // careful its own internal bookkeeping is - it simply doesn't
        // know about any other issue. Bug #10's fix: this is now the ONE
        // set for the whole batch (seeded here with this issue's own
        // already-on-file barcodes, same as before), not a new one per
        // issue - see AddEntriesAsIssues' batchClaimedBarcodes.
        foreach (var existingBarcode in existingIssue.Covers.Select(c => c.Barcode))
            claimedBarcodes.Add(existingBarcode);
        var existingArtists = existingIssue.Covers
            .Where(c => !string.IsNullOrWhiteSpace(c.CoverArtist))
            .Select(c => c.CoverArtist!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Backfills the Issue-level Publisher too, same spirit as the
        // per-cover placeholder upgrade below - added 2026-11-24 after a
        // real Lunar backfill run left Publisher/UPC blank on books that
        // pre-dated the Lunar import (a Metron-sourced issue's Publisher
        // is only ever set by its separate detail-enrichment call - see
        // IMetronService's own doc comment on why the cheap list call
        // can't carry it - so an issue whose enrichment never ran, or
        // hasn't yet, sits with this blank indefinitely otherwise). Only
        // ever fills a BLANK Publisher - never overwrites one already set.
        if (string.IsNullOrWhiteSpace(existingIssue.Publisher))
        {
            var publisherFromGroup = group.Select(e => e.Publisher).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (publisherFromGroup != null)
                existingIssue.Publisher = publisherFromGroup;
        }

        var candidates = new List<(Cover Cover, bool IsGuess)>();
        var enrichedCovers = 0;
        // Bug #12 fix (2026-11-24): claimedBarcodes now also carries every
        // barcode already in the Covers table system-wide (see
        // AddEntriesAsIssues' batchClaimedBarcodes doc comment), not just
        // this issue's own + this batch's. That means the skip just below
        // can now ALSO fire for the completely normal case of this issue's
        // OWN already-on-file barcode being seen again on a later scrape -
        // ownBarcodesSnapshot (captured before any seeding) tells the two
        // cases apart so only a genuine cross-issue conflict gets logged.
        var ownBarcodesSnapshot = existingIssue.Covers.Select(c => c.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Tracks barcodes already claimed by something earlier in THIS SAME
        // call's own group (a sibling cover-letter variant of this issue,
        // discovered just now) - separate from ownBarcodesSnapshot (already
        // on file before this call) and from claimedBarcodes itself (which
        // also carries every OTHER issue's barcodes DB/batch-wide). Many
        // publishers (confirmed: Marvel/DC-heavy data) assign the SAME
        // barcode to every cover-letter variant of one issue - a UPC
        // identifies the product (issue+price), not the individual print
        // cover - but Covers.Barcode has a DB-level UNIQUE index. Without
        // this distinction, a sibling variant discovered in this same group
        // collided with claimedBarcodes right after its first sibling
        // claimed the shared barcode, and got logged + dropped as a false
        // "different issue" conflict (2026-11-25/26 fix, see
        // claude/metron-integration-notes.md).
        var thisGroupBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in group)
        {
            foreach (var (cover, isGuess) in BuildCoversFromEntry(e, seriesName, existingIssue.IssueNumber, barcodePrefix, result))
            {
                if (claimedBarcodes.Contains(cover.Barcode))
                {
                    // IMPORTANT: this is checked by (letter, barcode) PAIR,
                    // not barcode alone - because every sibling variant of
                    // this same issue carries the IDENTICAL barcode when a
                    // publisher shares one UPC across covers (see the big
                    // comment above), checking barcode alone would also
                    // match Cover A's own already-on-file barcode against
                    // every one of its siblings' candidates, and silently
                    // drop every single one of them as a false "already on
                    // file" no-op - exactly the bug this derive-and-keep
                    // fix exists to prevent. Only a genuine re-scrape of
                    // the SAME letter with the SAME barcode is a true no-op.
                    bool alreadyExactlyOnFile = existingIssue.Covers.Any(c =>
                        string.Equals(c.Barcode, cover.Barcode, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.CoverLetter, cover.CoverLetter, StringComparison.OrdinalIgnoreCase));
                    if (alreadyExactlyOnFile)
                    {
                        continue;
                    }
                    // A sibling variant legitimately shares this barcode -
                    // either one already on file before this call started
                    // (ownBarcodesSnapshot - e.g. Cover A was enriched in an
                    // earlier run and its siblings are only being discovered
                    // now), or one claimed by an earlier row in THIS SAME
                    // call's own loop (thisGroupBarcodes - e.g. Cover A is
                    // also brand new this call). Either way, keep it under
                    // its own unique value instead of dropping it. '#' never
                    // appears in a real numeric barcode, so the real
                    // barcode stays recoverable as the part before it (used
                    // by BackfillCoversFromPrhImageCdn and any future
                    // barcode/UPC lookup).
                    if (ownBarcodesSnapshot.Contains(cover.Barcode) || thisGroupBarcodes.Contains(cover.Barcode))
                    {
                        var derived = $"{cover.Barcode}#{cover.CoverLetter}";
                        var suffix = 2;
                        while (!claimedBarcodes.Add(derived)) { derived = $"{cover.Barcode}#{cover.CoverLetter}{suffix}"; suffix++; }
                        cover.Barcode = derived;
                        // falls through below - this candidate proceeds
                        // exactly like one that never collided at all.
                    }
                    else
                    {
                        result.Errors.Add($"{seriesName} #{existingIssue.IssueNumber:0.##}: skipped a cover (barcode {cover.Barcode}) - already in use by a different issue in the system; needs a manual look.");
                        continue;
                    }
                }
                else
                {
                    thisGroupBarcodes.Add(cover.Barcode);
                }

                if (!isGuess && existingLetters.Contains(cover.CoverLetter))
                {
                    // Not a genuinely new variant - this is a richer
                    // re-scrape of a letter already on file. Used to just
                    // `continue` here and quietly throw the better data
                    // away, which is exactly how a book discovered early
                    // via Metron's cheap list call (real UPC/price only
                    // ever arrive from its separate, sometimes-failed
                    // detail call) could sit with a placeholder Cover
                    // forever even after a MUCH richer Lunar row for the
                    // same cover came through later - confirmed live
                    // 2026-11-24. Upgrade the existing placeholder instead
                    // of discarding the new data, but only when the
                    // existing one actually looks like a placeholder
                    // (LooksLikePlaceholderCover) - a cover that's already
                    // been properly enriched from a trustworthy source is
                    // left alone rather than blindly overwritten by
                    // whatever the next source happens to say.
                    var existingCover = existingIssue.Covers.First(c =>
                        string.Equals(c.CoverLetter, cover.CoverLetter, StringComparison.OrdinalIgnoreCase));
                    if (LooksLikePlaceholderCover(existingCover))
                    {
                        existingCover.Barcode = cover.Barcode;
                        existingCover.Price = cover.Price;
                        if (string.IsNullOrWhiteSpace(existingCover.Description)) existingCover.Description = cover.Description;
                        if (string.IsNullOrWhiteSpace(existingCover.CoverArtist)) existingCover.CoverArtist = cover.CoverArtist;
                        if (string.IsNullOrWhiteSpace(existingCover.CoverImagePath)) existingCover.CoverImagePath = cover.CoverImagePath;
                        // Claim it now, not just at the top of the method -
                        // this is the actual fix (see claimedBarcodes' own
                        // doc comment above): a later row in this same
                        // group that happens to carry this same barcode
                        // must no longer be free to grab it too.
                        claimedBarcodes.Add(cover.Barcode);
                        enrichedCovers++;
                    }
                    continue;
                }
                if (isGuess && !string.IsNullOrWhiteSpace(cover.CoverArtist) && existingArtists.Contains(cover.CoverArtist)) continue;
                candidates.Add((cover, isGuess));
                claimedBarcodes.Add(cover.Barcode);
            }
        }

        if (candidates.Count > 0)
        {
            ResolveCoverLetters(candidates, existingLetters);
            foreach (var (cover, _) in candidates)
                existingIssue.Covers.Add(cover);
            result.Added.Add($"{seriesName} #{existingIssue.IssueNumber:0.##}: +{candidates.Count} new cover variant(s) found on a later scrape.");
        }

        if (enrichedCovers > 0)
            result.Added.Add($"{seriesName} #{existingIssue.IssueNumber:0.##}: filled in real price/UPC for {enrichedCovers} placeholder cover(s).");
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

    // Used to hard-cap this to today +/- 3 months. (2026-11-26: user
    // reported the New Books page's own "All Months" dropdown still
    // topped out around 2845 issues no matter what, when the DB actually
    // has 7141+ PRH-sourced issues alone - traced to this method, not the
    // page.) The page's month tabs and "All Months" selector only ever
    // group/filter WHATEVER this method already returned - they can't see
    // past a bound imposed here. Returning everything lets both UIs'
    // existing "All Months" option (and their month-tab lists) reach
    // every month actually in the database, not just a fixed window
    // around today. The page still defaults to showing the current
    // month's tab on load (that logic lives in NewBooks.razor/
    // AutoFetchControl.cs, unchanged) - this only removes the invisible
    // ceiling underneath it.
    public List<Issue> GetNewBooks()
    {
        return _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .ToList();
    }

    // Raised from 400 to 600 on 2026-11-24 after "12-Foot Skeleton #2" was
    // reported as still visibly blurry when zoomed, even though its cover
    // (a Lunar-embedded photo, not a Comic Vine medium_url) was NOT caught by
    // the original 400px line. Root cause: the zoom overlay (site.js) blows
    // any zoomed cover up to as much as 1200px wide, so a cover needs to be
    // roughly half that (600px) to still look reasonably sharp once
    // stretched that far - 400px was only ever tuned to catch Comic Vine's
    // specific old "medium_url" size, not to predict what looks good at the
    // zoom's own ceiling. Still deliberately conservative on the matching
    // side: the gates below (an exact issue-number match on Comic Vine, after
    // auto-linking the series if needed) mean a cover under this width from
    // some other source just gets skipped rather than mis-replaced, so
    // there's no real cost to erring wide here - widening this constant only
    // means more covers get a chance to be checked, never a riskier check.
    private const int LowResCoverWidthThreshold = 600;

    // Retroactive one-time upgrade pass - see IAutoFetchService.
    // UpgradeLowResolutionCovers's own doc comment for the full story (both
    // ComicVineService issue-fetch methods used to save covers at Comic
    // Vine's smaller "medium_url" size; this is the catch-up for whatever
    // got saved that way before today's fix). Mirrors RetryMetronEnrichment's
    // own shape (trace log, per-item try/catch, onProgress once per item,
    // SaveChanges every 5 + at the end, limit for a Test-N button).
    //
    // Follow-up (2026-11-24, same day): the first version of this method
    // only ever attempted an upgrade for a series ALREADY linked to a Comic
    // Vine volume (Series.ComicVineVolumeId) - user reported this "does
    // nothing" for covers that came from the Lunar .xlsx importer, since a
    // Lunar-only series is never auto-linked to Comic Vine by that import
    // path at all (Lunar embeds its own cover photo directly - there was
    // never a reason to also look the series up on Comic Vine). That's most
    // of this store's low-res covers, not an edge case. Fix: reuses the
    // SAME conservative TryAutoLink/PickBestVolumeMatch logic the normal
    // weekly Check Now flow already uses for exactly this (an exact
    // case-insensitive name match is required, with issue-count/start-year
    // as the tiebreak - see TryAutoLink's own doc comment for the full
    // "never guess" rule, including the Superman/Wonder Woman wrong-volume
    // bugs that rule exists to prevent) - a confident link found here is
    // just as real and permanent as one found by the normal discovery flow,
    // and benefits every FUTURE run (new-issue checks, future cover
    // backfills), not just this one cover. Same 24h AutoLinkRetryCooldown
    // respected too, so a TP/GN-style series Comic Vine will never carry
    // doesn't get re-asked on every single run of this button.
    public AutoFetchResult UpgradeLowResolutionCovers(Action<string>? onProgress = null, int? limit = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Upgrade low-resolution covers run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + (limit.HasValue ? $" (TEST RUN - capped at {limit} cover(s))" : ""));
        trace.AppendLine(new string('=', 60));

        var candidates = _db.Covers
            .Include(c => c.Issue).ThenInclude(i => i.Series)
            .Where(c => c.CoverImagePath != null && c.CoverImagePath != "")
            .ToList();
        if (limit.HasValue) candidates = candidates.Take(limit.Value).ToList();

        onProgress?.Invoke($"Checking {candidates.Count} cover(s) on file for low-resolution images...");

        var done = 0;
        var skippedNotLowRes = 0;
        var skippedNoVolumeLink = 0;
        var skippedAutoLinkCooldown = 0;
        var skippedNoExactMatch = 0;
        var skippedRateLimited = 0;
        var newlyLinked = 0;
        var comicVineRateLimited = false;
        foreach (var cover in candidates)
        {
            done++;
            var series = cover.Issue.Series;
            var label = $"{series.Name} #{cover.Issue.IssueNumber:0.##} ({cover.CoverLetter})";
            string outcomeLine;

            try
            {
                if (!File.Exists(cover.CoverImagePath!))
                {
                    outcomeLine = $"{done}/{candidates.Count} {label}: file on disk is missing - skipped.";
                    trace.AppendLine(outcomeLine);
                    onProgress?.Invoke(outcomeLine);
                    continue;
                }

                var dims = ImageDimensionReader.TryReadDimensions(cover.CoverImagePath!);
                if (dims == null || dims.Value.Width >= LowResCoverWidthThreshold)
                {
                    skippedNotLowRes++;
                    continue; // already a decent size (or unreadable) - nothing to do, not worth a trace line per cover
                }

                // Once Comic Vine is confirmed rate-limited, every further
                // call to it this run (auto-link OR the cover lookup below)
                // would just fail the same way - skip without even trying,
                // same spirit as RescanExistingBooks' own comicVineRateLimited.
                if (comicVineRateLimited)
                {
                    skippedRateLimited++;
                    continue;
                }

                if (series.ComicVineVolumeId == null)
                {
                    if (series.LastAutoLinkAttempt != null &&
                        DateTime.Now - series.LastAutoLinkAttempt.Value < AutoLinkRetryCooldown)
                    {
                        skippedAutoLinkCooldown++;
                        continue; // tried to link this one recently and it didn't match - don't retry yet
                    }

                    if (!TryAutoLink(series, result))
                    {
                        skippedNoVolumeLink++;
                        continue; // no confident Comic Vine match for this series at all (e.g. a Lunar-only title Comic Vine doesn't carry, or a collected-edition name) - leave the existing cover alone
                    }

                    newlyLinked++;
                }

                var betterUrl = _comicVine.FindBestCoverImageUrl(series.ComicVineVolumeId!.Value, cover.Issue.IssueNumber);
                if (string.IsNullOrWhiteSpace(betterUrl))
                {
                    skippedNoExactMatch++;
                    continue; // series is Comic-Vine-linked, but no confident exact ISSUE match there - leave the existing (low-res but real) cover alone
                }

                var hint = $"{series.Name}_{cover.Issue.IssueNumber}_{cover.CoverLetter}_hires";
                var newPath = _comicVine.DownloadCoverImage(betterUrl, hint);
                if (string.IsNullOrWhiteSpace(newPath))
                {
                    outcomeLine = $"{done}/{candidates.Count} {label}: found a higher-res URL but the download failed - left the existing cover in place.";
                    trace.AppendLine(outcomeLine);
                    onProgress?.Invoke(outcomeLine);
                    continue;
                }

                cover.CoverImagePath = newPath;
                result.CoversBackfilled++;
                outcomeLine = $"{done}/{candidates.Count} {label}: upgraded ({dims.Value.Width}px -> higher resolution).";
                result.Added.Add(outcomeLine);
                trace.AppendLine(outcomeLine);
                onProgress?.Invoke(outcomeLine);
            }
            catch (Exception ex) when (IsRateLimitError(ex))
            {
                comicVineRateLimited = true;
                skippedRateLimited++;
                outcomeLine = $"{done}/{candidates.Count} {label}: Comic Vine's rate limit is active - skipping it for the rest of this run (covers already upgraded above are kept; the rest can be retried later).";
                result.Errors.Add(outcomeLine);
                trace.AppendLine(outcomeLine);
                onProgress?.Invoke(outcomeLine);
            }
            catch (Exception ex)
            {
                outcomeLine = $"{done}/{candidates.Count} {label}: upgrade failed - {ex.Message}";
                result.Errors.Add(outcomeLine);
                trace.AppendLine(outcomeLine);
                onProgress?.Invoke(outcomeLine);
            }

            if (done % 5 == 0 || done == candidates.Count)
            {
                var (saved, saveError) = TrySaveChanges();
                if (!saved) result.Errors.Add($"Batch ending at cover {done}: failed to save - {saveError}");
            }
        }

        trace.AppendLine();
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Upgraded {result.CoversBackfilled} cover(s) ({newlyLinked} series newly linked to Comic Vine along the way); {skippedNotLowRes} already a decent size, {skippedNoVolumeLink} had no confident Comic Vine match for the series, {skippedAutoLinkCooldown} skipped (auto-link tried recently and failed), {skippedNoExactMatch} had no confident exact issue match, {skippedRateLimited} skipped due to Comic Vine's rate limit.");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);

        return result;
    }

    // Added 2026-11-25/26, right after Bug #15's Comic-Vine-auto-link fix
    // shipped for "lots of covers missing" - the user pushed back ("ok but
    // the pdfs do have covers"), correctly pointing out that PRH's own PDF
    // catalog clearly shows real cover art, which doesn't square with "PRH's
    // feed never includes a cover image." Investigated live in the browser
    // rather than assuming the PDF embeds its own images: the /downloadpdf/
    // page's own docgen request carries no per-title artwork at all (its
    // header_img field is just the same kind of static branding blob the
    // xls flow's xls_logo already is). The real source turned out to be
    // completely independent of the docgen chain and much simpler: every
    // product thumbnail on prhcomics.com's own catalog pages - confirmed for
    // BOTH ISBN-13 collected editions and PRH's single-issue "CB" format
    // rows, not just one or the other - loads from a plain, public,
    // unauthenticated image CDN keyed directly by the item's own barcode:
    // https://images.penguinrandomhouse.com/cover/{barcode}. No cookies, no
    // nonce, no admin-ajax round trip - confirmed live with
    // credentials:'omit'. Confirmed it wants the barcode EXACTLY as PRH's
    // own feed stores it - a bare 13-digit ISBN-13 for a collected edition,
    // or the full 17-digit UPC+5-digit-addon for a single issue (truncating
    // that down to the bare 12-digit UPC always misses). When nothing's on
    // file for a code it answers HTTP 200 with a generic stock graphic
    // instead of a 404 - confirmed live this is byte-for-byte identical
    // (same SHA256) across several different missing codes, so it's folded
    // into the SAME PlaceholderImageRegistry ComicVineService.
    // DownloadCoverImage already uses for comicade.com's own "not ready yet"
    // stock graphic, rather than inventing a second parallel mechanism - see
    // FetchPrhPlaceholderHashIfUnknown below.
    //
    // Candidate query is barcode-SHAPE based, not provenance-based (there's
    // still no "which source added this" column on Cover - same gap noted
    // throughout this file): any cover with nothing on disk yet whose
    // Barcode is purely numeric and either 13 or 17 digits long. That
    // naturally catches every PRH-sourced cover (PRH stores the real
    // ISBN/UPC straight into Barcode, never a generated fallback the way
    // Lunar does for a blank UPC column - see LunarDistributionParser) while
    // leaving every MTR-/LOCG-/LUN- placeholder barcode alone (none of
    // those are purely numeric) - and isn't actually limited to PRH-sourced
    // covers at all: any OTHER source's cover that happens to carry a real
    // ISBN-13 or a full 17-digit UPC+addon as its own barcode can get a hit
    // here too, since this turned out to be Penguin Random House's own
    // general-purpose book-cover image service, not something scoped to
    // comics specifically. limit caps how many candidates get processed in
    // one run, same Test-N-button contract as RetryMetronEnrichment/
    // UpgradeLowResolutionCovers above. Never run automatically.
    public AutoFetchResult BackfillCoversFromPrhImageCdn(Action<string>? onProgress = null, int? limit = null)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Backfill covers from PRH's image CDN run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + (limit.HasValue ? $" (TEST RUN - capped at {limit} cover(s))" : ""));
        trace.AppendLine(new string('=', 60));

        // Length check runs in SQL (translatable); the purely-numeric check
        // runs afterward, in memory, against the already-materialized list -
        // string.All(char.IsDigit) isn't something EF Core's LINQ provider
        // can translate, and running it as part of the query itself would
        // throw at runtime instead of silently client-evaluating (confirmed
        // against this project's EF Core version elsewhere in this file).
        //
        // A cover's own Barcode isn't always the clean 13/17-digit value
        // itself any more (2026-11-25/26 fix, see
        // claude/metron-integration-notes.md for the full writeup): a
        // sibling cover-variant that shares its real barcode with another
        // cover of the SAME issue (common industry practice - a UPC
        // identifies the issue+price, not the individual print cover) now
        // gets a derived barcode of the form "{realBarcode}#{marker}"
        // instead of being silently dropped at import time, since
        // Covers.Barcode has a DB-level unique index and can't store the
        // same value twice. '#' never appears in a real numeric barcode,
        // so splitting on it and using the part BEFORE it recovers the
        // real CDN lookup key either way - a clean, un-suffixed barcode
        // just splits into itself, so this one path handles both shapes.
        var rawCandidates = _db.Covers
            .Include(c => c.Issue).ThenInclude(i => i.Series)
            .Where(c => string.IsNullOrWhiteSpace(c.CoverImagePath) && c.Barcode != null && c.Barcode != "")
            .ToList();

        var candidates = rawCandidates
            .Select(c => (Cover: c, LookupBarcode: c.Barcode!.Split('#')[0]))
            .Where(x => (x.LookupBarcode.Length == 13 || x.LookupBarcode.Length == 17) && x.LookupBarcode.All(char.IsDigit))
            // Same candidate-selection ordering fix already proven for
            // RetryMetronEnrichment/RetryLocgEnrichment's own "worst five
            // picks" bug: unordered (DB scan/Id order) just reflects
            // whatever order the PRH import happened to insert rows in -
            // for a 67-catalog backfill that's roughly "oldest catalog
            // first," which is exactly the kind of old, possibly-never-
            // illustrated solicitation a Test-N run shouldn't be judged
            // against. Closest-to-today by absolute distance surfaces
            // recently-shipped or about-to-ship real issues instead, the
            // same as every other retry/backfill method in this file.
            .OrderBy(x => x.Cover.Issue.ReleaseDate.HasValue
                ? Math.Abs((x.Cover.Issue.ReleaseDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.Now).TotalDays)
                : double.MaxValue)
            .ToList();
        if (limit.HasValue) candidates = candidates.Take(limit.Value).ToList();

        onProgress?.Invoke($"Checking {candidates.Count} cover(s) with an ISBN/UPC-shaped barcode against PRH's own cover image CDN...");

        FetchPrhPlaceholderHashIfUnknown();

        var done = 0;
        var noCoverAvailable = 0;
        foreach (var (cover, lookupBarcode) in candidates)
        {
            done++;
            var series = cover.Issue.Series;
            var label = $"{series.Name} #{cover.Issue.IssueNumber:0.##} ({cover.CoverLetter})";
            string outcomeLine;

            try
            {
                var bytes = PrhWebsiteFetcher.DownloadCoverBytesAsync(lookupBarcode).GetAwaiter().GetResult();
                if (bytes == null)
                {
                    outcomeLine = $"{done}/{candidates.Count} {label}: download failed - left uncovered.";
                    trace.AppendLine(outcomeLine);
                    onProgress?.Invoke(outcomeLine);
                    continue;
                }

                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (PlaceholderImageRegistry.IsKnownPlaceholder(hash))
                {
                    noCoverAvailable++;
                    continue; // PRH has nothing on file for this barcode yet - not an error, not worth a trace line per cover
                }

                var hint = $"{series.Name}_{cover.Issue.IssueNumber}_{cover.CoverLetter}_prh-isbn";
                cover.CoverImagePath = _comicVine.SaveCoverImageBytes(bytes, hint);
                result.CoversBackfilled++;
                outcomeLine = $"{done}/{candidates.Count} {label}: found a cover via PRH's image CDN ({lookupBarcode}).";
                result.Added.Add(outcomeLine);
                trace.AppendLine(outcomeLine);
                onProgress?.Invoke(outcomeLine);
            }
            catch (Exception ex)
            {
                outcomeLine = $"{done}/{candidates.Count} {label}: {ex.Message}";
                result.Errors.Add(outcomeLine);
                trace.AppendLine(outcomeLine);
                onProgress?.Invoke(outcomeLine);
            }

            if (done % 25 == 0 || done == candidates.Count)
            {
                var (saved, saveError) = TrySaveChanges();
                if (!saved) result.Errors.Add($"Batch ending at cover {done}: failed to save - {saveError}");
            }
        }

        trace.AppendLine();
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Found {result.CoversBackfilled} cover(s) via PRH's image CDN; {noCoverAvailable} had nothing on file there yet; {result.Errors.Count} error(s).");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);

        return result;
    }

    // Teaches PlaceholderImageRegistry the PRH image CDN's "nothing on file
    // for this barcode yet" stock-graphic hash, if it isn't already known -
    // called once at the top of BackfillCoversFromPrhImageCdn so the very
    // first real miss in that run's own loop always has something to
    // compare against (without this, the first miss would have nothing to
    // match yet and would get saved as if it were real art). Best-effort:
    // if this one lookup fails, the run still works correctly for anything
    // already in the registry from comicade.com, and self-corrects the next
    // time ClearPlaceholderCovers runs.
    private static void FetchPrhPlaceholderHashIfUnknown()
    {
        try
        {
            var bootstrapBytes = PrhWebsiteFetcher.DownloadCoverBytesAsync("0000000000000").GetAwaiter().GetResult();
            if (bootstrapBytes == null) return;
            var bootstrapHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bootstrapBytes));
            PlaceholderImageRegistry.Add(bootstrapHash);
        }
        catch
        {
            // Best-effort - see doc comment above.
        }
    }

    // Shared by BackfillCoverVariantsFromPrhBookPages (the manual,
    // whole-catalog backfill pass below) and ImportPrhCatalogRelease
    // (which now calls this itself, right after every monthly import -
    // see that method's own doc comment for why). The same shape test
    // either way: a PRH-shaped barcode (real ISBN-13, or a 17-digit
    // UPC+price-addon) is what tells a genuinely PRH-sourced barcode
    // apart from every other source's generated placeholder/fallback,
    // and is also exactly what's needed to find this issue's own
    // prhcomics.com product page in the first place (see
    // PrhWebsiteFetcher.DownloadBookPageHtmlAsync).
    private static bool IsPrhShapedBarcode(string barcode)
    {
        var lookup = barcode.Split('#')[0];
        return (lookup.Length == 13 || lookup.Length == 17) && lookup.All(char.IsDigit);
    }

    // PRH's own "Additional covers" carousel labels a variant "Variant
    // B (Artist)" or, for a ratio-incentive copy, "Variant RI (10)
    // (Artist)" - never "Cover B"/"CVR B" the way the catalog's own
    // Title column does (confirmed live 2026-11-26, Godzilla Rivals:
    // Mothra Vs. M.O.G.U.E.R.A. #1's own 2 real variants). PrhTitleParser.
    // CoverLetterMarker only recognizes the catalog's own wording and is
    // deliberately left alone here (shared with the catalog importer,
    // already proven for that different grammar) - these two are this
    // scrape path's own fallback, checked only when that one found
    // nothing, so a "Cover X"/"CVR X"-worded carousel item (if PRH ever
    // writes one that way) still goes through the original path
    // unchanged.
    private static readonly Regex CarouselVariantRiMarker = new(@"\bVariant\s+RI\s*(?:\((\d+)\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CarouselVariantLetterMarker = new(@"\bVariant\s+([A-Za-z])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CarouselTrailingArtist = new(@"\(([^)]+)\)\s*$", RegexOptions.Compiled);

    // Third naming style, found 2026-11-26 on Amazing Spider-Man #25's
    // real page ("AMAZING SPIDER-MAN #25 MARK CHIARELLO VARIANT [DS]"):
    // the artist comes BEFORE the bare word "Variant", with no letter
    // or "RI" at all - neither regex above matches this. Only tried
    // when both of those fail. Captures whatever sits between the
    // issue's own "#N" and "Variant" (artist name, sometimes with a
    // short descriptor like "Classified Target") and uses it as BOTH
    // the cover letter and the artist credit, since there's no real
    // letter to fall back on and a named variant is still its own
    // distinct cover, not a dupe of Cover A.
    private static readonly Regex CarouselArtistBeforeVariant = new(@"#\s*\d+(?:\.\d+)?\s+(.+?)\s+Variant\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fourth naming style, found 2026-11-27 (real run: Midnight X-Men
    // #2's own "Additional covers" carousel). Unlike every style above,
    // this one carries NO series name and NO "#N" at all - just
    // "COVER E JOSHUA SWABY MIDNIGHT GALLERY 1:25" on its own, confirmed
    // live by re-fetching prhcomics.com's own page for this exact issue
    // (its OTHER sibling covers on the same carousel came through with
    // the full "MIDNIGHT X-MEN #2 COVER F ... VARIANT [1:50]" wording,
    // so this short form isn't universal - PRH's own markup is simply
    // inconsistent about it, seemingly tied to which specific cover a
    // request happens to land on). With no series/issue# for
    // PrhTitleParser.ApplyTitle to anchor on, the whole raw title falls
    // into SeriesName with nothing usable left for CoverLetter, which is
    // what produced 478 false "already in use by a different issue"
    // errors in one run - every single one checked turned out to
    // already be this SAME issue's own real cover (see the barcode
    // check right before the claimedBarcodes conflict check below,
    // which is the actual fix for those 478; this regex is the
    // complementary half, so a brand-new cover using this short wording
    // gets a clean single-letter CoverLetter instead of the whole raw
    // string).
    private static readonly Regex CarouselCoverLetterPrefix = new(@"^\s*(?:CVR|Cover)\s+([A-Za-z])\b\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Shared by BackfillCoverVariantsFromPrhBookPages and
    // ImportPrhCatalogRelease - given ONE issue's own prhcomics.com
    // book-page HTML (the caller does the actual HTTP fetch and its own
    // exception/404 handling, since what happens on THOSE outcomes
    // differs a little between a one-off import batch and a whole-
    // catalog backfill run), parses its "Additional covers" section and
    // adds/upgrades whatever it finds. Returns (added, upgraded) - both
    // 0 for a real page with nothing extra listed, which is the
    // ordinary, common case for a genuinely single-cover issue, not a
    // problem worth a trace line of its own.
    private (int Added, int Upgraded) ApplyAdditionalCoversFromPrhBookPageHtml(
        Issue issue, string html, HashSet<string> claimedBarcodes,
        AutoFetchResult result, System.Text.StringBuilder trace, string label)
    {
        var scraped = PrhWebsiteFetcher.ParseAdditionalCovers(html);
        if (scraped.Count == 0) return (0, 0);

        var addedHere = 0;
        var upgradedHere = 0;
        var alreadyKnownHere = 0;
        string? unrelatedRawTitle = null;
        float? unrelatedIssueNumber = null;

        // See the seriesLooksUnrelated check below for why this is
        // needed alongside the issue-number check, not instead of it.
        var seriesKeyExpected = SeriesNameKey.Normalize(issue.Series.Name).ToUpperInvariant();

        foreach (var item in scraped)
        {
            // Reuses PrhTitleParser as-is - the scraped title is in
            // the exact same "SERIES #N COVER X ARTIST VARIANT"
            // grammar as the catalog importer's own Title column
            // (confirmed live, identical wording), so there's no new
            // parsing grammar to get wrong here. row.IssueNumber is
            // read back only as a sanity check (this scrape should
            // always describe THIS SAME issue, since it came from one
            // of this issue's own covers' page) - a mismatch means
            // prhcomics.com linked to a different issue/printing than
            // expected, so it's skipped rather than risk filing a
            // cover under the wrong issue.
            //
            // Confirmed live 2026-11-26 (real Test 5 run, The Amazing
            // Spider-Man #1004 and Star Wars: The Book of Boba Fett
            // #6): when an issue has NO real additional covers, the
            // page's "Additional covers" container renders empty and
            // ParseAdditionalCovers' 60000-char forward window keeps
            // going past it and scrapes the NEXT carousel on the page
            // instead (a "more issues in this series" widget, which
            // reuses the exact same carousel-meta-isbn/title markup) -
            // every single item it finds there is a different issue
            // number, never a genuine cover of THIS issue. Since a
            // real "Additional covers" carousel only ever lists
            // siblings of the SAME issue, the first title that comes
            // back for a DIFFERENT issue number is the signal we've
            // crossed into that next, unrelated carousel - everything
            // from here on is assumed to be more of the same, so this
            // breaks out of the loop entirely rather than logging one
            // "doesn't match" line per remaining item (confirmed live:
            // Boba Fett #6 alone would otherwise have logged 7 of
            // them). This is the ordinary, expected shape for the
            // (common) single-cover issue, not a problem - so it's
            // just a trace line, never result.Errors.
            var row = new PrhCatalogRow();
            PrhTitleParser.ApplyTitle(row, item.RawTitle);

            // Added 2026-11-26 (bug report: a PRH catalog import run
            // where several scrapes "rolled into an unrelated
            // carousel", one of them landing on a DIFFERENT SERIES
            // entirely - a Godzilla Rivals one-shot's own scrape rolling
            // into a Teenage Mutant Ninja Turtles carousel item, caught
            // below only because THAT item happened to carry an
            // explicit "#3"). The issue-number check alone has a blind
            // spot: PrhTitleParser.ApplyTitle defaults a "#N"-less
            // title's IssueNumber to 1 (built for a true catalog one-
            // shot row), so an unrelated one-shot/OGN by the same
            // author with no "#N" of its own silently "matches"
            // whenever the issue being checked is ALSO #1 - exactly the
            // population most likely to hit this scrape path in the
            // first place (confirmed live: prhcomics.com's own
            // "Additional covers" carousel fell straight through into
            // an "Other Books by this Author" carousel for this issue,
            // and its first two, genuinely-this-issue entries -
            // "Variant B", "Variant RI (10)" - both have no "#N" of
            // their own either, so they never would have tripped this
            // check regardless). seriesLooksUnrelated is a second,
            // independent guard that doesn't replace the issue-number
            // check (still useful on its own, e.g. two issues of the
            // SAME series) - it just closes the gap that one can't see
            // through on its own.
            var seriesKeyScraped = string.IsNullOrWhiteSpace(row.SeriesName)
                ? ""
                : SeriesNameKey.Normalize(row.SeriesName).ToUpperInvariant();
            var seriesLooksUnrelated = seriesKeyScraped.Length > 0
                && !seriesKeyScraped.Contains(seriesKeyExpected, StringComparison.Ordinal)
                && !seriesKeyExpected.Contains(seriesKeyScraped, StringComparison.Ordinal);

            if (seriesLooksUnrelated || (row.IssueNumber.HasValue && Math.Abs(row.IssueNumber.Value - issue.IssueNumber) > 0.01f))
            {
                // Don't log yet - whether this is a genuine "nothing
                // here" or just "already fully covered, nothing new"
                // depends on alreadyKnownHere, decided once after the
                // loop (see the bottom of this method) so the two
                // cases get distinct, non-misleading trace lines
                // instead of this single message covering both.
                unrelatedRawTitle = item.RawTitle;
                unrelatedIssueNumber = row.IssueNumber;
                break;
            }

            // PrhTitleParser.ApplyTitle can't split "Variant B
            // (Vasquez)" or "Variant RI (10) (Height)" apart the way it
            // splits the catalog's own "Cover B (Vasquez)" wording (its
            // CoverLetterMarker never matches "Variant", so the whole
            // string falls into SeriesName with nothing left over for
            // VariantText) - without this, every one of these would
            // silently default to coverLetter "A" and collide with the
            // Main Cover already on file instead of becoming its own
            // cover. Checked first, falling back to row.CoverLetter
            // (PrhTitleParser's own result) only if neither matches, so
            // a "Cover X"-worded carousel item still works the old way.
            var coverLetter = row.CoverLetter;
            string? carouselVariantArtist = null;
            var coverPrefixMatch = CarouselCoverLetterPrefix.Match(item.RawTitle);
            var carouselMarkerFound = coverPrefixMatch.Success;
            if (coverPrefixMatch.Success)
            {
                coverLetter = coverPrefixMatch.Groups[1].Value.ToUpperInvariant();
                var prefixRemainder = coverPrefixMatch.Groups[2].Value.Trim();
                if (prefixRemainder.Length > 0) carouselVariantArtist = prefixRemainder;
            }
            else
            {
                var riMatch = CarouselVariantRiMarker.Match(item.RawTitle);
                carouselMarkerFound = riMatch.Success;
                if (riMatch.Success)
                {
                    coverLetter = riMatch.Groups[1].Success ? $"RI ({riMatch.Groups[1].Value})" : "RI";
                }
                else
                {
                    var letterMatch = CarouselVariantLetterMarker.Match(item.RawTitle);
                    if (letterMatch.Success)
                    {
                        coverLetter = letterMatch.Groups[1].Value.ToUpperInvariant();
                        carouselMarkerFound = true;
                    }
                    else
                    {
                        var namedMatch = CarouselArtistBeforeVariant.Match(item.RawTitle);
                        var namedText = namedMatch.Success ? namedMatch.Groups[1].Value.Trim() : "";
                        if (namedText.Length > 0)
                        {
                            coverLetter = namedText;
                            carouselVariantArtist = namedText;
                            carouselMarkerFound = true;
                        }
                    }
                }
            }
            if (carouselMarkerFound && carouselVariantArtist is null)
            {
                var artistMatch = CarouselTrailingArtist.Match(item.RawTitle);
                if (artistMatch.Success) carouselVariantArtist = artistMatch.Groups[1].Value.Trim();
            }
            decimal price = 0;
            if (!string.IsNullOrWhiteSpace(item.PriceText))
            {
                var priceDigits = item.PriceText.Replace("$", "").Replace("US", "", StringComparison.OrdinalIgnoreCase).Trim();
                decimal.TryParse(priceDigits, NumberStyles.Number, CultureInfo.InvariantCulture, out price);
            }

            var existingCover = issue.Covers.FirstOrDefault(c => string.Equals(c.CoverLetter, coverLetter, StringComparison.OrdinalIgnoreCase));
            if (existingCover != null)
            {
                // Already have this letter - only worth touching if
                // what's on file isn't a real, distinct barcode yet:
                // Bug #16's own derived "{barcode}#{letter}" fallback
                // ('#' never appears in a real numeric barcode - see
                // BackfillCoversFromPrhImageCdn's own doc comment), or
                // one of the other sources' generated placeholder (see
                // LooksLikePlaceholderCover). A cover that already has
                // its own clean, real, different barcode is left
                // alone even though it differs from this scrape -
                // never overwrite good data blind.
                var looksDerived = existingCover.Barcode.Contains('#');
                if ((looksDerived || LooksLikePlaceholderCover(existingCover)) && !claimedBarcodes.Contains(item.Isbn))
                {
                    existingCover.Barcode = item.Isbn;
                    if (price > 0) existingCover.Price = price;
                    var upgradeArtist = carouselVariantArtist ?? row.VariantText;
                    if (string.IsNullOrWhiteSpace(existingCover.CoverArtist) && !string.IsNullOrWhiteSpace(upgradeArtist))
                        existingCover.CoverArtist = upgradeArtist;
                    claimedBarcodes.Add(item.Isbn);
                    upgradedHere++;
                    result.CoversBackfilled++;
                }
                else
                {
                    // Already have this exact real cover - not new,
                    // but not "nothing here" either. See alreadyKnownHere.
                    alreadyKnownHere++;
                }
                continue;
            }

            // Bug found 2026-11-27 (real run: 478 "skipped ... already in
            // use by a different issue" lines in one overnight backfill),
            // clustered around multi-cover one-shots like the "Midnight"
            // trio, VENOM: POV, Punisher Showdowns, Doomquest, Iron Man
            // #12 - every single one checked turned out to be a FALSE
            // alarm: the barcode was already filed on THIS SAME issue,
            // just under a cover letter this particular scrape didn't
            // resolve (confirmed live against the real DB: barcode
            // 75960621668000216 already belongs to Midnight X-Men #2's
            // own Cover "E", not some other issue). claimedBarcodes only
            // says a barcode is claimed by SOMEONE in this run/database -
            // it doesn't say by whom, so a barcode that's actually
            // already on THIS issue (just unmatched by the coverLetter
            // string above) was being misreported as a conflict with "a
            // different issue." Checked first, before ever calling it
            // that: if it's already one of this issue's own covers,
            // under ANY letter, that's not a conflict at all.
            if (issue.Covers.Any(c => string.Equals(c.Barcode, item.Isbn, StringComparison.OrdinalIgnoreCase)))
            {
                alreadyKnownHere++;
                continue;
            }

            if (claimedBarcodes.Contains(item.Isbn))
            {
                var conflictLine = $"{label}: skipped additional cover {coverLetter} (barcode {item.Isbn}) - already in use by a different issue; needs a manual look.";
                result.Errors.Add(conflictLine);
                trace.AppendLine(conflictLine);
                continue;
            }

            var newCover = new Cover
            {
                CoverLetter = coverLetter,
                Barcode = item.Isbn,
                Price = price,
                StockQuantity = 0,
                CoverArtist = carouselVariantArtist ?? row.VariantText
            };
            issue.Covers.Add(newCover);
            claimedBarcodes.Add(item.Isbn);
            addedHere++;
            result.CoverVariantsAdded++;
        }

        // Decided once, here, instead of inline above: a mismatch
        // only means "this book genuinely has no more covers" when
        // nothing real was found at all. If real siblings WERE found
        // this call (added, upgraded, or already fully on file from
        // an earlier run), the mismatch just marks where the real
        // carousel ended - it's not itself news, except when every
        // single sibling was already known, which is worth saying
        // explicitly rather than leaving silent.
        if (addedHere == 0 && upgradedHere == 0)
        {
            if (alreadyKnownHere > 0)
                trace.AppendLine($"{label}: already has {alreadyKnownHere} real cover(s) matching PRH's own page - nothing new to add.");
            else if (unrelatedRawTitle != null)
                trace.AppendLine($"{label}: additional-cover scrape rolled into an unrelated carousel at \"{unrelatedRawTitle}\" (issue #{unrelatedIssueNumber}) - stopping here, no real additional covers found.");
        }

        return (addedHere, upgradedHere);
    }

    // See IAutoFetchService.BackfillCoverVariantsFromPrhBookPages's own
    // doc comment for the full story. Candidate issues are chosen by
    // IsPrhShapedBarcode - the SAME "does this issue have a cover whose
    // barcode is PRH's own real ISBN-13/17-digit-UPC shape" test as
    // BackfillCoversFromPrhImageCdn's own candidate query just above
    // (reused for the identical reason: that shape is what tells a
    // genuinely PRH-sourced barcode apart from every other source's
    // generated placeholder/fallback) - that same barcode is also
    // exactly what's needed to find this issue's own prhcomics.com
    // product page in the first place. When an issue already has more
    // than one such cover, "A" is preferred as the one actually fetched -
    // arbitrary but deterministic, and it doesn't matter which cover's
    // page gets fetched, since every cover's own "Additional covers"
    // carousel lists every OTHER cover of the same issue, never itself.
    //
    // IMPORTANT: this is no longer the only way new books get their
    // variant covers. ImportPrhCatalogRelease now calls
    // FetchAdditionalCoversForImportedEntries itself, right after every
    // monthly import, which does the exact same lookup for just that
    // batch's own newly-touched issues - see that method's own doc
    // comment for the "why no reason to go to LOCG at all for this"
    // story (2026-11-26). This method stays as the WHOLE-CATALOG catch-
    // up pass, for anything imported before that existed, or any issue
    // whose prhcomics.com page didn't have its variants listed yet at
    // import time but does now.
    public AutoFetchResult BackfillCoverVariantsFromPrhBookPages(Action<string>? onProgress = null, int? limit = null) =>
        BackfillCoverVariantsFromPrhBookPages(onProgress, limit, onlyIssueIds: null);

    // onlyIssueIds: when set, check just these issues (Fetch Everything's
    // retry of failed checks) instead of every issue in the database.
    private AutoFetchResult BackfillCoverVariantsFromPrhBookPages(Action<string>? onProgress, int? limit, IReadOnlyCollection<int>? onlyIssueIds)
    {
        var result = new AutoFetchResult();
        var trace = new System.Text.StringBuilder();
        trace.AppendLine($"Backfill cover variants from PRH's own book pages run at {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + (limit.HasValue ? $" (TEST RUN - capped at {limit} issue(s))" : ""));
        trace.AppendLine(new string('=', 60));

        var rawIssues = _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .Where(i => i.Covers.Any())
            .Where(i => onlyIssueIds == null || onlyIssueIds.Contains(i.Id))
            .ToList();

        var candidates = rawIssues
            .Select(i => (
                Issue: i,
                QueryCover: i.Covers
                    .Where(c => IsPrhShapedBarcode(c.Barcode))
                    .OrderBy(c => string.Equals(c.CoverLetter, "A", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(c => c.CoverLetter, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()))
            .Where(x => x.QueryCover != null)
            // Same closest-to-today ordering fix as every other retry/
            // backfill method in this file (see BackfillCoversFromPrhImageCdn's
            // own doc comment), surfaces recently-shipped/about-to-ship
            // issues first on a capped Test-N run, instead of whatever DB
            // insertion order happens to be.
            .OrderBy(x => x.Issue.ReleaseDate.HasValue
                ? Math.Abs((x.Issue.ReleaseDate.Value.ToDateTime(TimeOnly.MinValue) - DateTime.Now).TotalDays)
                : double.MaxValue)
            .ToList();
        if (limit.HasValue) candidates = candidates.Take(limit.Value).ToList();

        onProgress?.Invoke($"Checking {candidates.Count} issue(s) that have a PRH-shaped barcode on file against prhcomics.com's own book pages...");

        // Batch-wide, same spirit as AddMissingCoverVariants' own
        // claimedBarcodes - seeded with every barcode already in the
        // table so a real cross-issue collision (should be vanishingly
        // rare here, since PRH's own additional-covers data carries a
        // genuinely distinct barcode per cover) is still caught rather
        // than silently violating the DB's unique index.
        var claimedBarcodes = _db.Covers.Select(c => c.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var done = 0;
        var noPrhPageFound = 0;
        var consecutiveNetworkFailures = 0;
        foreach (var (issue, queryCover) in candidates)
        {
            done++;
            var series = issue.Series;
            var label = $"{series.Name} #{issue.IssueNumber:0.##}";
            var lookupBarcode = queryCover!.Barcode.Split('#')[0];

            // Added 2026-11-26 after the user flagged a real risk while a
            // full uncapped run was already in progress: this method had
            // ZERO pacing between requests, unlike every other scraper in
            // this file (Metron's own documented 20 req/min limit enforced
            // as a 3.5s wait; LOCG's multi-minute-per-week rate limit) -
            // and an unthrottled multi-thousand-request burst against one
            // site in a short window is exactly the traffic shape that got
            // Metron's own API access IP-blocked earlier in this project
            // (see that section above). prhcomics.com's book page has no
            // documented rate limit to match against the way Metron's API
            // does, so this is a deliberately modest, precautionary pace
            // rather than a measured one - skipped on the very first
            // request (nothing to be polite about pacing against yet).
            if (done > 1) Thread.Sleep(300);

            string? html;
            try
            {
                html = PrhWebsiteFetcher.DownloadBookPageHtmlAsync(lookupBarcode).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var errLine = $"{done}/{candidates.Count} {label}: {ex.Message}";
                result.Errors.Add(errLine);
                trace.AppendLine(errLine);
                onProgress?.Invoke(errLine);
                if (PrhWebsiteFetcher.IsNetworkFailure(ex) && ++consecutiveNetworkFailures >= PrhWebsiteFetcher.MaxConsecutiveNetworkFailures)
                {
                    var stopLine = $"STOPPED EARLY at {done}/{candidates.Count}: {consecutiveNetworkFailures} network failures in a row - the connection looks down. Nothing past this point was checked; re-run this button once you're back online.";
                    result.Errors.Add(stopLine);
                    trace.AppendLine(stopLine);
                    onProgress?.Invoke(stopLine);
                    var (savedStop, saveErrorStop) = TrySaveChanges();
                    if (!savedStop) result.Errors.Add($"Batch ending at issue {done}: failed to save - {saveErrorStop}");
                    break;
                }
                continue;
            }
            consecutiveNetworkFailures = 0;

            if (html == null)
            {
                // A genuine HTTP 404 - this barcode isn't actually a
                // prhcomics.com product (see DownloadBookPageHtmlAsync's
                // own doc comment) - not an error, not worth a trace line
                // per miss, same spirit as BackfillCoversFromPrhImageCdn's
                // own noCoverAvailable.
                noPrhPageFound++;
                if (done % 25 == 0 || done == candidates.Count)
                {
                    var (saved0, saveError0) = TrySaveChanges();
                    if (!saved0) result.Errors.Add($"Batch ending at issue {done}: failed to save - {saveError0}");
                }
                continue;
            }

            var (addedHere, upgradedHere) = ApplyAdditionalCoversFromPrhBookPageHtml(issue, html, claimedBarcodes, result, trace, label);

            if (addedHere > 0 || upgradedHere > 0)
            {
                var outcomeLine = $"{done}/{candidates.Count} {label}: found {addedHere} new cover(s), upgraded {upgradedHere} barcode(s) via PRH's own book page ({lookupBarcode}).";
                result.Added.Add(outcomeLine);
                trace.AppendLine(outcomeLine);
                onProgress?.Invoke(outcomeLine);
            }

            if (done % 25 == 0 || done == candidates.Count)
            {
                var (saved, saveError) = TrySaveChanges();
                if (!saved) result.Errors.Add($"Batch ending at issue {done}: failed to save - {saveError}");
            }
        }

        trace.AppendLine();
        trace.AppendLine($"Finished at {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Found {result.CoverVariantsAdded} new cover(s) and upgraded {result.CoversBackfilled} existing barcode(s) via PRH's own book pages; {noPrhPageFound} candidate(s) weren't real prhcomics.com products; {result.Errors.Count} error(s).");
        result.Trace = trace.ToString();
        DebugLog.WriteCheckNowLog(result.Trace);

        return result;
    }

    // Added 2026-11-26: the user pointed out that the catalog xlsx's own
    // UPC cell is ITSELF a hyperlink straight to this exact issue's
    // prhcomics.com book page (confirmed live - a Sabrina the Teenage
    // Witch #5 variant row's UPC hyperlinked to
    // prhcomics.com/book/?isbn=<that same barcode>#additional_covers,
    // whose "Additional covers" grid listed all 7 siblings: Main Cover,
    // 2 more open-order variants, 1 blank-sketch variant, and 3
    // incentive variants - for an ONI PRESS book, not DC, confirming
    // this lookup is publisher-agnostic, not DC/PRH-imprint-specific as
    // first assumed) - and that the real pipeline should be "get the
    // month's xlsx, get each book's metadata, then get its covers/
    // variants from that same UPC lookup, done" as ONE pass, not a
    // separate manual catch-up click run some time later. So: no LOCG
    // involvement needed at all for this (LOCG's own 30-second crawl-
    // delay would have made a whole-catalog pass take DAYS; prhcomics.com
    // has no such limit - see BackfillCoverVariantsFromPrhBookPages'
    // own 300ms courtesy-pacing comment above - so doing this inline,
    // synchronously, as part of every import is entirely practical).
    //
    // Called by ImportPrhCatalogRelease right after its own save
    // succeeds. Re-derives which Issues this batch actually touched by
    // matching entries' own (SeriesName, IssueNumber) back against
    // allSeries - the same list ImportPrhCatalogRelease already built
    // and fed into AddEntriesAsIssues, which adds new Series/Issues onto
    // it in place - rather than changing AddEntriesAsIssues' own
    // signature to additionally return touched Issues, which is shared
    // by every other import source in this file (Lunar, Comicade, LOCG,
    // Metron, GCD) and not worth the risk of touching for this. Scoped
    // to just this one batch rather than the whole catalog, so a
    // monthly import (typically a few hundred rows) stays fast - no
    // separate manual "Backfill PRH Cover Variants" click needed for
    // anything imported from here on.
    private void FetchAdditionalCoversForImportedEntries(
        List<ComicListEntry> entries, List<Series> allSeries,
        AutoFetchResult result, System.Text.StringBuilder trace, Action<string>? onProgress)
    {
        // Keys compare the normalized name UPPERCASED, not with a
        // StringComparer on the HashSet itself - ValueTuple<string,
        // float>'s own default equality is plain ordinal (case-
        // SENSITIVE) per field, with no way to hand it a per-field
        // comparer, and this absolutely needs to be case-insensitive:
        // Lunar/PRH's own all-caps catalog style ("AMAZING SPIDER-MAN")
        // vs. an existing Series.Name in whatever casing it was first
        // created with ("Amazing Spider-Man") describe the same series
        // (see SeriesNameKey's own doc comment - Normalize only strips
        // "The "/subtitle punctuation, it never touches casing at all).
        var wantedKeys = entries
            .Where(e => e.SeriesName != null && e.IssueNumber.HasValue)
            .Select(e => (Key: SeriesNameKey.Normalize(e.SeriesName!).ToUpperInvariant(), e.IssueNumber!.Value))
            .ToHashSet();

        var touchedIssues = allSeries
            .SelectMany(s => s.Issues)
            .Where(i => wantedKeys.Contains((SeriesNameKey.Normalize(i.Series.Name).ToUpperInvariant(), i.IssueNumber)))
            .Distinct()
            .ToList();

        var candidates = touchedIssues
            .Select(i => (
                Issue: i,
                QueryCover: i.Covers
                    .Where(c => IsPrhShapedBarcode(c.Barcode))
                    .OrderBy(c => string.Equals(c.CoverLetter, "A", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(c => c.CoverLetter, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()))
            .Where(x => x.QueryCover != null)
            .ToList();

        if (candidates.Count == 0) return;

        onProgress?.Invoke($"Checking {candidates.Count} just-imported issue(s) against prhcomics.com's own book pages for additional covers...");

        var claimedBarcodes = _db.Covers.Select(c => c.Barcode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var noPrhPageFound = 0;
        var consecutiveNetworkFailures = 0;
        // Every candidate starts "pending" and is cleared once PRH answers
        // (page or 404), so errors and anything skipped by an early stop stay.
        _prhPageChecksPending?.UnionWith(candidates.Select(c => c.Issue.Id));

        foreach (var (issue, queryCover) in candidates)
        {
            done++;
            var label = $"{issue.Series.Name} #{issue.IssueNumber:0.##}";
            var lookupBarcode = queryCover!.Barcode.Split('#')[0];

            // Same deliberately modest, precautionary pace as
            // BackfillCoverVariantsFromPrhBookPages - see that method's
            // own comment on why.
            if (done > 1) Thread.Sleep(300);

            string? html;
            try
            {
                html = PrhWebsiteFetcher.DownloadBookPageHtmlAsync(lookupBarcode).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var errLine = $"{label}: additional-covers lookup failed: {ex.Message}";
                result.Errors.Add(errLine);
                trace.AppendLine(errLine);
                if (PrhWebsiteFetcher.IsNetworkFailure(ex) && ++consecutiveNetworkFailures >= PrhWebsiteFetcher.MaxConsecutiveNetworkFailures)
                {
                    var stopLine = $"STOPPED additional-covers check early at {done}/{candidates.Count}: {consecutiveNetworkFailures} network failures in a row - the connection looks down. Run Backfill PRH Cover Variants once you're back online to catch the rest.";
                    result.Errors.Add(stopLine);
                    trace.AppendLine(stopLine);
                    onProgress?.Invoke(stopLine);
                    break;
                }
                continue;
            }
            consecutiveNetworkFailures = 0;
            _prhPageChecksPending?.Remove(issue.Id);

            if (html == null)
            {
                // A genuine 404 - this barcode isn't actually a
                // prhcomics.com product (happens for a non-comic-book
                // item some catalogs include, or a title PRH hasn't
                // published a page for yet) - not an error, same spirit
                // as BackfillCoverVariantsFromPrhBookPages' own.
                noPrhPageFound++;
                continue;
            }

            var (addedHere, upgradedHere) = ApplyAdditionalCoversFromPrhBookPageHtml(issue, html, claimedBarcodes, result, trace, label);

            if (addedHere > 0 || upgradedHere > 0)
            {
                var outcomeLine = $"{label}: found {addedHere} new cover(s), upgraded {upgradedHere} barcode(s) via PRH's own book page ({lookupBarcode}).";
                result.Added.Add(outcomeLine);
                trace.AppendLine(outcomeLine);
                onProgress?.Invoke(outcomeLine);
            }
        }

        trace.AppendLine($"Checked {candidates.Count} just-imported issue(s) for additional covers; {noPrhPageFound} weren't real prhcomics.com products.");

        // Covers every PRH-shaped-barcode cover among THIS batch's own
        // touched issues that doesn't have an image yet - not just the
        // variants the loop above just added, but also each new issue's
        // own original "Cover A" row straight from the catalog row
        // itself, which previously had no image at all until a separate,
        // manual Comic Vine auto-link/backfill pass eventually found one
        // (often a lower-resolution or wrong-printing image - see
        // claude/metron-integration-notes.md's own pixelation writeup)
        // or never did for a series Comic Vine couldn't confidently
        // match at all. Same CDN BackfillCoversFromPrhImageCdn already
        // uses (images.penguinrandomhouse.com/cover/{barcode} - see that
        // method's own doc comment for the full story on how this was
        // found and why it's safe/publisher-agnostic), just scoped to
        // this one import batch instead of a whole-catalog scan, and run
        // automatically here instead of only from its own manual button.
        var imageCandidates = touchedIssues
            .SelectMany(i => i.Covers.Select(c => (Issue: i, Cover: c)))
            .Select(x => (x.Issue, x.Cover, LookupBarcode: x.Cover.Barcode.Split('#')[0]))
            .Where(x => string.IsNullOrWhiteSpace(x.Cover.CoverImagePath)
                && (x.LookupBarcode.Length == 13 || x.LookupBarcode.Length == 17)
                && x.LookupBarcode.All(char.IsDigit))
            .ToList();

        if (imageCandidates.Count > 0)
        {
            onProgress?.Invoke($"Checking {imageCandidates.Count} cover(s) from this import against PRH's own cover image CDN...");
            FetchPrhPlaceholderHashIfUnknown();

            var noCoverAvailable = 0;
            foreach (var (issue, cover, lookupBarcode) in imageCandidates)
            {
                var label = $"{issue.Series.Name} #{issue.IssueNumber:0.##} ({cover.CoverLetter})";

                try
                {
                    var bytes = PrhWebsiteFetcher.DownloadCoverBytesAsync(lookupBarcode).GetAwaiter().GetResult();
                    if (bytes == null) continue; // download failed - left uncovered, same as BackfillCoversFromPrhImageCdn's own

                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                    if (PlaceholderImageRegistry.IsKnownPlaceholder(hash))
                    {
                        noCoverAvailable++;
                        continue; // PRH has nothing on file for this barcode yet
                    }

                    var hint = $"{issue.Series.Name}_{issue.IssueNumber}_{cover.CoverLetter}_prh-isbn";
                    cover.CoverImagePath = _comicVine.SaveCoverImageBytes(bytes, hint);
                    result.CoversBackfilled++;
                    var outcomeLine = $"{label}: found a cover via PRH's image CDN ({lookupBarcode}).";
                    trace.AppendLine(outcomeLine);
                    onProgress?.Invoke(outcomeLine);
                }
                catch (Exception ex)
                {
                    var errLine = $"{label}: cover image CDN lookup failed: {ex.Message}";
                    result.Errors.Add(errLine);
                    trace.AppendLine(errLine);
                }
            }

            trace.AppendLine($"Checked {imageCandidates.Count} cover(s) from this import against PRH's image CDN; {noCoverAvailable} had nothing on file there yet.");
        }

        var (saved, saveError) = TrySaveChanges();
        if (!saved) result.Errors.Add($"Failed to save this import batch's additional-covers/cover-image lookup: {saveError}");
    }
}

// Tiny helper for AutoFetchService.FetchLunarReleaseFromWebsite - reading
// the homepage HTML to find today's current release/FOC dates is a
// two-line regex job, not worth pulling in HtmlAgilityPack (already used
// elsewhere for comicade.com) or a whole separate service class/interface
// the way ComicadeFocService got, since nothing else in the app needs to
// talk to lunardistribution.com's website directly - everything else
// about Lunar is just reading an .xlsx file, wherever it came from.
internal static class LunarWebsiteFetcher
{
    public static readonly HttpClient Http = BuildClient();

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        // Was 30s, which is plenty for the homepage HTML and the small
        // weekly release file (~68KB) but not for the FOC file - that one
        // covers a much longer solicited horizon and runs several MB
        // (6.6MB in testing), confirmed timing out for real on
        // 2026-11-24 ("The request was canceled due to the configured
        // HttpClient.Timeout of 30 seconds elapsing"). 300s gives real
        // headroom for a slower connection without the download hanging
        // forever if the site is genuinely unreachable.
        client.Timeout = TimeSpan.FromSeconds(300);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComicPos/1.0 (personal single-store inventory tool)");
        return client;
    }

    // Finds <button ... class="datafile release" ... data-val="MM/dd/yyyy
    // h:mm:ss AM/PM" ...> (or class="datafile foc") in the homepage's
    // plain server-rendered HTML - confirmed present with zero JavaScript
    // execution via a credentials-omitted fetch() against the live site
    // on 2026-11-24 (see metron-integration-notes.md), so a simple regex
    // on the class attribute's exact text is enough; both buttons were
    // observed with "datafile release"/"datafile foc" in that fixed
    // order every time, and this only needs two values, not a full HTML
    // parser.
    public static string? FindDataVal(string html, string kind)
    {
        var pattern = new Regex(
            $@"<button[^>]*\bclass=""datafile\s+{kind}""[^>]*\bdata-val=""([^""]+)""",
            RegexOptions.IgnoreCase);
        var m = pattern.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }
}
// Replicates, from plain C# HttpClient calls, the exact multi-step AJAX
// chain prhcomics.com's own "Download Excel Spreadsheet" button performs
// for its monthly "Comics Retail" catalog - confirmed live, step by
// step, via the built-in browser's network/JS inspection tools on
// 2026-11-25/26 against
// https://prhcomics.com/catalog-landing-page/?catalogCode=2026-12 (see
// claude/metron-integration-notes.md for the full write-up). No PRH
// account/login is required anywhere in this chain - confirmed via
// window.prhbl_user_is_logged_in === false on a totally anonymous page
// load that still completed every step below successfully. It's the
// exact same public flow anyone gets just by visiting the page and
// clicking the button; it's simply WordPress-AJAX-and-nonce-driven
// rather than a single static file URL the way Lunar's is.
//
// The chain, each step confirmed with a real request/response in the
// browser before being ported here:
//   1. GET the catalog-landing-page itself. Four things are scraped
//      straight out of its rendered HTML by finding each one's
//      JS-literal assignment in the page's own inline <script> and
//      reading the quoted text that follows (not by executing any
//      JavaScript - these are present as plain quoted text in the raw
//      HTML the server sends, so a plain indexOf-based scrape is
//      enough, same "look for the literal, not a full parser" approach
//      LunarWebsiteFetcher.FindDataVal above already uses):
//        - "product_load_nonce" - a standard WordPress nonce from a
//          hidden <input>, tied to this page load, NOT to any login/
//          session - a brand-new anonymous GET always yields a fresh,
//          directly usable one.
//        - "xls_logo" / "xls_template" - PRH's own branding image and
//          blank Excel template, both embedded directly as base64
//          literals in the page's own script rather than hosted at any
//          separate URL. Scraped fresh on every run instead of being
//          hardcoded here, so this keeps working automatically if PRH
//          ever reskins either one.
//        - "title_list_product_module_api_url" - a fixed per-book API
//          URL template the docgen service wants passed through
//          untouched; scraped the same way for the same reason.
//   2. POST wp/wp-admin/admin-ajax.php, action=get_product_list, using
//      that nonce - returns an "api_url"/"api_args" pair describing, in
//      PRH's own internal title-API terms, exactly which titles this
//      catalog code covers. This is PRH's own backend already doing the
//      filtering/query-building - passed straight through unread in
//      step 4.
//   3. POST wp/wp-admin/admin-ajax.php, action=get_rest_api_hash, using
//      the SAME nonce - returns a short-lived "rest_api_hash" that
//      authorizes the final docgen call below.
//   4. POST https://sales-docgen.prh.com/wp-json/prh-rest-api/v1/download-xls
//      - a separate REST microservice (NOT on prhcomics.com) that takes
//      the api_url/api_args from step 2, the hash from step 3, and the
//      logo/template scraped in step 1, and returns
//      {"xls_filename": "...", "xls_base64_encoded": "..."} - a
//      ready-to-write .xlsx file, base64-encoded. The nested
//      "post_params"/"xls_title_mapper"/"file_download_name" fields are
//      sent as PHP-style bracketed form fields (post_params[action]=...
//      etc.) because that's literally what the real site's own jQuery
//      sends for a nested object in a form POST (confirmed by reading
//      the raw outgoing request body in the browser, not guessed) - a
//      plain JSON body here would not be parsed the same way server-side.
internal static class PrhWebsiteFetcher
{
    public static readonly HttpClient Http = BuildClient();

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(120);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComicPos/1.0 (personal single-store inventory tool)");
        return client;
    }

    private const string AjaxUrl = "https://prhcomics.com/wp/wp-admin/admin-ajax.php";
    private const string DownloadXlsUrl = "https://sales-docgen.prh.com/wp-json/prh-rest-api/v1/download-xls";
    private const int PostId = 11811;

    public static async Task<byte[]> DownloadCatalogXlsxAsync(string catalogCode, Action<string>? onProgress)
    {
        var pageUrl = $"https://prhcomics.com/catalog-landing-page/?catalogCode={Uri.EscapeDataString(catalogCode)}";

        onProgress?.Invoke($"Reading prhcomics.com's catalog page for {catalogCode}...");
        var html = await Http.GetStringAsync(pageUrl).ConfigureAwait(false);

        // The page's own "titlelist-load-nonce" hidden input always ships
        // BLANK in the raw server HTML - confirmed live 2026-11-26 (see
        // metron-integration-notes.md). The real nonce only appears after
        // the page's own jQuery runs and POSTs action=get_nonce to this
        // same admin-ajax.php endpoint, writing the result into that
        // input client-side. That bootstrap call needs no page context,
        // cookies, or catalogCode at all - confirmed it works completely
        // standalone - so it's fetched directly here instead of scraped
        // from this (always-empty) HTML. This replaces the original
        // (wrong) approach, which only ever appeared to work when tested
        // against a browser tab that had already run that bootstrap call
        // moments earlier - a fresh HttpClient run here always failed
        // with the site's own "Data not available. Authorization
        // failed!" error until this fix.
        onProgress?.Invoke("Getting a fresh request token...");
        var nonce = await FetchBootstrapNonceAsync().ConfigureAwait(false);
        var xlsLogo = ExtractLiteral(html, "xls_logo")
            ?? throw new InvalidOperationException("Couldn't find the page's embedded logo image - prhcomics.com's layout may have changed.");
        var xlsTemplate = ExtractLiteral(html, "xls_template")
            ?? throw new InvalidOperationException("Couldn't find the page's embedded spreadsheet template - prhcomics.com's layout may have changed.");
        var titleListApiUrl = ExtractLiteral(html, "title_list_product_module_api_url")
            ?? throw new InvalidOperationException("Couldn't find the page's title-list API URL template - prhcomics.com's layout may have changed.");
        titleListApiUrl = System.Net.WebUtility.HtmlDecode(titleListApiUrl);

        var paramsJson = $"{{\"source-page\":\"catalog-landing-page\",\"catalogCode\":\"{catalogCode}\"}}";
        const string filtersJson = "{\"l1_category\":\"\",\"filters\":{\"category\":[],\"sale-status\":[],\"format\":[],\"age\":[],\"grade\":[],\"guides\":[],\"publisher\":[],\"comics_publisher\":[]}}";

        onProgress?.Invoke($"Asking prhcomics.com which titles the {catalogCode} catalog covers...");
        var productListForm = new Dictionary<string, string>
        {
            ["product_load_nonce"] = nonce,
            ["action"] = "get_product_list",
            ["postType"] = "page",
            ["postId"] = PostId.ToString(CultureInfo.InvariantCulture),
            ["isbns"] = "[]",
            ["params"] = paramsJson,
            ["filters"] = filtersJson,
            ["layout"] = "grid-lg",
            ["sort"] = ""
        };

        string apiUrl;
        string apiArgsJson;
        using (var productListResp = await Http.PostAsync(AjaxUrl, new FormUrlEncodedContent(productListForm)).ConfigureAwait(false))
        {
            productListResp.EnsureSuccessStatusCode();
            var productListJson = await productListResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var productListDoc = JsonDocument.Parse(productListJson);
            var root = productListDoc.RootElement;
            if (!root.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
                throw new InvalidOperationException($"get_product_list didn't return success: {productListJson}");

            var data = root.GetProperty("data");
            apiUrl = data.GetProperty("api_url").GetString() ?? "";
            var apiArgsEl = data.GetProperty("api_args");
            apiArgsJson = apiArgsEl.ValueKind == JsonValueKind.String ? apiArgsEl.GetString() ?? "{}" : apiArgsEl.GetRawText();

            if (data.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt32(out var total))
                onProgress?.Invoke($"Catalog {catalogCode} covers {total} title(s) - requesting the spreadsheet...");
        }

        onProgress?.Invoke("Getting a download authorization token...");
        string restHash;
        using (var hashResp = await Http.PostAsync(AjaxUrl, new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["action"] = "get_rest_api_hash",
                   ["_ajax_nonce"] = nonce
               })).ConfigureAwait(false))
        {
            hashResp.EnsureSuccessStatusCode();
            var hashJson = await hashResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var hashDoc = JsonDocument.Parse(hashJson);
            if (!hashDoc.RootElement.TryGetProperty("success", out var hashSuccessEl) || !hashSuccessEl.GetBoolean())
                throw new InvalidOperationException($"get_rest_api_hash didn't return success: {hashJson}");
            restHash = hashDoc.RootElement.GetProperty("data").GetProperty("hash").GetString() ?? "";
        }

        onProgress?.Invoke("Building the spreadsheet...");

        // Mirrors the SAME dict sent to get_product_list above, with
        // "download": true added and a fixed default sort - confirmed
        // live that the site's own download call adds exactly this one
        // extra field, nothing else differs from the list-fetch params.
        var postParamsForDownload = new (string Key, string Value)[]
        {
            ("product_load_nonce", nonce),
            ("action", "get_product_list"),
            ("postType", "page"),
            ("postId", PostId.ToString(CultureInfo.InvariantCulture)),
            ("isbns", "[]"),
            ("params", paramsJson),
            ("filters", filtersJson),
            ("download", "true"),
            ("sort", "frontlistiest_onsale:desc")
        };

        var titleMapper = new (string Key, string Value)[]
        {
            ("excel_header_subtitle", "Subtitle"),
            ("excel_header_title", "Title"),
            ("excel_header_author", "Creators"),
            ("excel_header_isbn", "ISBN/UPC"),
            ("excel_header_price_usd", "Retail Price (US)"),
            ("excel_header_price_can", "Retail Price (CAN)"),
            ("excel_header_format", "Format"),
            ("excel_header_on_sale_date", "On-Sale Date"),
            ("excel_header_foc_date", "FOC Date"),
            ("excel_header_series_frequency", "Series Frequency"),
            ("excel_header_format_code", "Format"),
            ("excel_header_division", "Division"),
            ("excel_header_imprint", "Imprint"),
            ("excel_header_age", "Age"),
            ("excel_header_grade_sap", "Grade"),
            ("excel_header_grade_edu", "Grade Edu"),
            ("excel_header_quantity", "Quantity")
        };

        var fileDownloadName = new (string Key, string Value)[]
        {
            ("type", "xls"),
            ("site_name", "PRH-Comics-Retail"),
            ("wish_list_override", "Pull List"),
            ("wish_list_slug", "pull-list")
        };

        var downloadForm = new List<KeyValuePair<string, string>>
        {
            new("api_url", apiUrl),
            new("api_args", apiArgsJson),
            new("api_map_key", "comics")
        };
        AddBracketed(downloadForm, "post_params", postParamsForDownload);
        downloadForm.Add(new("title_list_product_module_api_url", titleListApiUrl));
        downloadForm.Add(new("rest_api_hash", restHash));
        downloadForm.Add(new("title", $"Catalog {CatalogCodeToTitle(catalogCode)}"));
        downloadForm.Add(new("subtitle", ""));
        downloadForm.Add(new("clickable-url", pageUrl));
        downloadForm.Add(new("product-module-page-url", pageUrl));
        downloadForm.Add(new("file_type", "xls"));
        downloadForm.Add(new("xls_logo", xlsLogo));
        downloadForm.Add(new("xls_header_row", "2"));
        downloadForm.Add(new("xls_meta_keywords", ""));
        downloadForm.Add(new("xls_title", "Penguin Random House Wish List"));
        downloadForm.Add(new("xls_desc", ""));
        downloadForm.Add(new("fill_row_colors", "[{\"column_number\":\"2\",\"color\":\"FFFFD700\"}]"));
        AddBracketed(downloadForm, "xls_title_mapper", titleMapper);
        downloadForm.Add(new("xls_trim_us_can_from_price", "false"));
        downloadForm.Add(new("xls_suppress_digital_price", "false"));
        downloadForm.Add(new("xls_template", xlsTemplate));
        AddBracketed(downloadForm, "file_download_name", fileDownloadName);
        downloadForm.Add(new("request_books_base_url", "https://prhcomics.com"));

        using var downloadRequest = new HttpRequestMessage(HttpMethod.Post, DownloadXlsUrl)
        {
            Content = new FormUrlEncodedContent(downloadForm)
        };
        downloadRequest.Headers.Referrer = new Uri(pageUrl);
        downloadRequest.Headers.TryAddWithoutValidation("Origin", "https://prhcomics.com");

        using var downloadResp = await Http.SendAsync(downloadRequest).ConfigureAwait(false);
        downloadResp.EnsureSuccessStatusCode();
        var downloadJson = await downloadResp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var downloadDoc = JsonDocument.Parse(downloadJson);
        var xlsBase64 = downloadDoc.RootElement.TryGetProperty("xls_base64_encoded", out var xlsBase64El)
            ? xlsBase64El.GetString()
            : null;
        if (string.IsNullOrEmpty(xlsBase64))
            throw new InvalidOperationException($"The download call didn't return a file: {downloadJson}");

        return Convert.FromBase64String(xlsBase64);
    }

    private static void AddBracketed(List<KeyValuePair<string, string>> form, string prefix, (string Key, string Value)[] pairs)
    {
        foreach (var (key, value) in pairs)
            form.Add(new($"{prefix}[{key}]", value));
    }

    // "2026-12" -> "December 2026", matching the text the page's own
    // <title>/download dialog use - purely cosmetic (goes into the
    // generated spreadsheet's "title" field, not anything parsed back
    // out of it), so falls back to the raw code rather than throwing if
    // it's ever not that exact shape.
    private static string CatalogCodeToTitle(string catalogCode)
    {
        var parts = catalogCode.Split('-');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
            && month is >= 1 and <= 12)
        {
            return $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)} {year}";
        }
        return catalogCode;
    }

    // POSTs action=get_nonce to admin-ajax.php - the same bootstrap call
    // the catalog page's own jQuery makes on load (see the big comment on
    // DownloadCatalogXlsxAsync's nonce line above for how this was found
    // and confirmed). Confirmed live this returns the identical nonce a
    // real browser session gets, works with zero cookies, and needs no
    // catalogCode or page context - it's a flat, non-wrapped JSON object
    // ({"nonce":"...", "nonceNewsletter":"...", ...}), not the usual
    // {"success":true,"data":{...}} shape admin-ajax.php's other actions
    // here use, so it's read directly rather than via the success/data
    // pattern the other calls in this class check.
    private static async Task<string> FetchBootstrapNonceAsync()
    {
        using var resp = await Http.PostAsync(AjaxUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "get_nonce"
        })).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("nonce", out var nonceEl))
            throw new InvalidOperationException($"get_nonce didn't return a nonce: {json}");
        return nonceEl.GetString()
            ?? throw new InvalidOperationException($"get_nonce returned a null nonce: {json}");
    }

    // Discovers every catalog code prhcomics.com currently has a monthly
    // catalog for, by walking its own public archive listing
    // (catalog/category/monthly-catalogs/, paginated at /page/N) instead
    // of guessing a date range - added 2026-11-26 for
    // FetchAllAvailablePrhCatalogsFromWebsite below, after the store
    // asked for every available month, not just one at a time. Confirmed
    // live this archive currently runs from 2021-06 (when PRH's own
    // comics distribution began) through whichever month is newest right
    // now, and that walking past the real last page returns a genuine
    // HTTP 404 - that's the real stopping signal used here, with a
    // "no new codes on this page" check as a backup in case that ever
    // changes. Every page also repeats one extra link to whatever month
    // is currently open for ordering, regardless of which page you're
    // on - the HashSet naturally drops that repeat once it's already
    // been seen. Plain GETs, no cookies needed, same as the rest of this
    // class - this archive page showed no sign of Cloudflare/bot
    // detection in testing, same as everything else prhcomics.com-side.
    public static async Task<List<string>> DiscoverAvailableCatalogCodesAsync(Action<string>? onProgress)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var codeRegex = new Regex(@"catalogCode=(\d{4}-\d{2})", RegexOptions.IgnoreCase);

        // Generous safety cap - the real archive was 6 pages deep when
        // this was tested (2021-06 through 2026-12), so this leaves
        // roughly 8x headroom rather than relying solely on the 404/
        // no-new-codes checks below to stop the walk.
        for (var page = 1; page <= 50; page++)
        {
            var url = page == 1
                ? "https://prhcomics.com/catalog/category/monthly-catalogs/"
                : $"https://prhcomics.com/catalog/category/monthly-catalogs/page/{page}";

            onProgress?.Invoke($"Reading prhcomics.com's catalog archive (page {page})...");
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                break; // past the real last page - confirmed live this is a genuine 404, not an empty 200.

            var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var before = codes.Count;
            foreach (Match m in codeRegex.Matches(html))
                codes.Add(m.Groups[1].Value);

            if (codes.Count == before)
                break; // a real page with nothing new on it - treat as the end too, just in case 404 detection ever changes.
        }

        var sorted = codes.ToList();
        sorted.Sort(StringComparer.OrdinalIgnoreCase); // "YYYY-MM" text-sorts chronologically.
        return sorted;
    }

    // Plain, public, unauthenticated per-barcode cover image lookup - see
    // AutoFetchService.BackfillCoversFromPrhImageCdn's own doc comment for
    // how this was found (it's the same CDN prhcomics.com's own
    // product-grid thumbnails load from - nothing docgen-specific). Returns
    // the raw image bytes for ANY successful response, including PRH's own
    // "nothing on file for this code yet" stock graphic - the caller is the
    // one that knows about PlaceholderImageRegistry and decides what a
    // given hash means; this method just fetches bytes. Returns null only
    // on an outright HTTP failure - this endpoint answers HTTP 200, never a
    // 404, even for a completely made-up code (confirmed live), so null
    // here means a real network/transport problem, not "no cover."
    public static async Task<byte[]?> DownloadCoverBytesAsync(string barcode)
    {
        var url = $"https://images.penguinrandomhouse.com/cover/{Uri.EscapeDataString(barcode)}";
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    // One scraped row from a book page's own "Additional covers"
    // carousel - see ParseAdditionalCovers below. RawTitle is in PRH's
    // familiar "SERIES #N COVER X ARTIST VARIANT" grammar (same one
    // PrhTitleParser already parses for the catalog importer), so the
    // caller (AutoFetchService.BackfillCoverVariantsFromPrhBookPages)
    // reuses that class directly rather than this file inventing a
    // second parsing grammar for the same text shape.
    public sealed record PrhAdditionalCover(string Isbn, string RawTitle, string? PriceText);

    // Plain, public, unauthenticated per-product detail page - confirmed
    // live 2026-11-26 this needs no cookies/session at all (fetch()'d
    // with credentials:'omit' from a totally cold page load and it still
    // returned the full page, including the real "Additional covers"
    // carousel - not something a click/AJAX call lazily fills in after
    // the fact; it's already in the server-rendered HTML). isbn can be
    // ANY cover's own real barcode for the issue - PRH's own product
    // page always lists every OTHER cover of that same issue here, never
    // itself, so which sibling you start from doesn't matter. See
    // ParseAdditionalCovers below and AutoFetchService.
    // BackfillCoverVariantsFromPrhBookPages's own doc comment for the
    // full story of how this was found.
    // One blip (a single timeout) shouldn't kill a multi-hour pass, but 10
    // misses in a row means the connection is gone (2026-11-27: a Wi-Fi
    // drop turned into 2,002 "No such host" errors before this existed).
    public const int MaxConsecutiveNetworkFailures = 10;

    // True for "couldn't reach the site at all" (DNS failure, no route,
    // timeout). False for a real HTTP error status (500, 403...), where the
    // site did answer - EnsureSuccessStatusCode sets StatusCode on those.
    public static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } || ex is TaskCanceledException;

    public static async Task<string?> DownloadBookPageHtmlAsync(string isbn)
    {
        var url = $"https://prhcomics.com/book/?isbn={Uri.EscapeDataString(isbn)}";
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        // A genuine HTTP 404 here - confirmed live 2026-11-26 against real
        // candidates (Feral #28, Lost Fantasy #14, Absolute Wonder Woman
        // #26, all non-PRH-distributed titles) - means this barcode,
        // while shaped like a real PRH ISBN/UPC, isn't actually a
        // prhcomics.com product at all. Same root cause already called out
        // in BackfillCoversFromPrhImageCdn's own candidate-query doc
        // comment: the "13 or 17 digit numeric" shape test isn't
        // provenance-based, so a real UPC from a non-PRH-distributed
        // publisher (Image, or DC/other titles PRH doesn't carry) can
        // still pass it. Treated as a soft miss (null), not an error - the
        // same spirit as that method's own "nothing on file yet" case.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    // Pulls every sibling cover out of a book page's own "Additional
    // covers" carousel (confirmed live against several real titles
    // 2026-11-26, including an incentive cover with no final art yet -
    // see BackfillCoverVariantsFromPrhBookPages's own doc comment). The
    // carousel's markup repeats itself once in full (a second, hidden
    // copy for a different screen-width layout - confirmed live, byte-
    // for-byte identical to the first), so results are de-duplicated by
    // ISBN as they're found rather than trusting the page to only list
    // each sibling once. Each item's own isbn comes from a dedicated
    // data-component="carousel-meta-isbn" element rather than scraped out
    // of an <img alt="..."> attribute - the <img> markup itself turned
    // out to vary (a plain "Main Cover"-style item has no incentive-
    // ribbon SVG in front of it; an incentive one does), while the
    // data-component-tagged isbn/title/price elements stayed identically
    // shaped across every title tested. Returns an empty list (not an
    // error) for a genuinely single-cover issue - confirmed live this
    // section is simply absent from the page in that case.
    public static List<PrhAdditionalCover> ParseAdditionalCovers(string html)
    {
        var results = new List<PrhAdditionalCover>();
        var sectionIdx = html.IndexOf("Additional covers", StringComparison.Ordinal);
        if (sectionIdx < 0) return results;

        // Caps the scan window well past the end of a realistic carousel
        // (every title tested so far, including a 5-cover one, fit
        // comfortably inside this) rather than parsing the rest of the
        // page - prhcomics.com's own book pages run 500KB-1MB+ of HTML
        // total (confirmed live), most of it unrelated carousels further
        // down (related titles, etc.).
        var scopeLen = Math.Min(60000, html.Length - sectionIdx);
        var scope = html.Substring(sectionIdx, scopeLen);

        var isbnRegex = new Regex(@"data-component=""carousel-meta-isbn"">(\d+)<", RegexOptions.Compiled);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in isbnRegex.Matches(scope))
        {
            var isbn = m.Groups[1].Value;
            if (!seen.Add(isbn)) continue; // the mobile-layout repeat - see this method's own doc comment

            // The title/price elements for this same item sit BEFORE the
            // isbn element in the markup (title, then author, then isbn,
            // then price - confirmed live), so the match window looks
            // backward from the isbn hit, not forward.
            var windowStart = Math.Max(0, m.Index - 3000);
            var windowEnd = Math.Min(scope.Length, m.Index + 600);
            var block = scope.Substring(windowStart, windowEnd - windowStart);

            var titleMatch = Regex.Match(block, @"data-component=""carousel-meta-title"">\s*<a[^>]*>([^<]+)");
            var priceMatch = Regex.Match(block, @"price-usa[^>]*>\s*([^<]+?)\s*</span>");

            var rawTitle = titleMatch.Success ? System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim() : "";
            if (rawTitle.Length == 0) continue; // nothing usable to parse a cover letter out of - skip rather than guess "A"

            results.Add(new PrhAdditionalCover(
                isbn,
                rawTitle,
                priceMatch.Success ? priceMatch.Groups[1].Value.Trim() : null));
        }
        return results;
    }

    // Finds `'key': '....'` exactly as it appears in the page's own
    // inline <script> text and returns the quoted value, unescaped.
    // Every value captured this way is pure base64/URL text with no
    // embedded quotes, so a plain indexOf-to-next-"'," scan is reliable
    // - confirmed live against the real page before this was written
    // (see the class comment above).
    private static string? ExtractLiteral(string html, string key)
    {
        var marker = $"'{key}': '";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = html.IndexOf("',", start, StringComparison.Ordinal);
        return end < 0 ? null : html.Substring(start, end - start);
    }
}
