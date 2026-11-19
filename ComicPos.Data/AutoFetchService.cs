using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class AutoFetchService : IAutoFetchService
{
    private readonly ComicDbContext _db;
    private readonly IComicVineService _comicVine;
    private readonly IComicListService _comicList;
    private readonly IGcdService _gcd;
    private readonly IStoreSettingsService _storeSettings;

    public AutoFetchService(ComicDbContext db, IComicVineService comicVine, IComicListService comicList, IGcdService gcd, IStoreSettingsService storeSettings)
    {
        _db = db;
        _comicVine = comicVine;
        _comicList = comicList;
        _gcd = gcd;
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
            trace.AppendLine($"Final: checked {result.SeriesChecked} series, linked {result.SeriesAutoLinked}, added {result.NewIssuesAdded} new issue(s).");
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
            DebugLog.WriteCheckNowLog(trace.ToString());
        }
    }

    private void RunCheckForNewIssues(AutoFetchResult result, System.Text.StringBuilder trace)
    {
        var allSeries = _db.AllSeries.Include(s => s.Issues).ToList();
        trace.AppendLine($"Rechecking {allSeries.Count} series already in inventory...");

        foreach (var series in allSeries)
        {
            if (series.ComicVineVolumeId == null)
            {
                if (!TryAutoLink(series, result)) continue; // no confident match - leave unlinked, try again next time
            }

            CheckOneSeries(series, result);
        }
        trace.AppendLine($"  done - linked {result.SeriesAutoLinked}, {result.NewIssuesAdded} issue(s) so far from existing series.");

        // allSeries only covers books the store already carries. A series
        // that has never been carried has no row here at all, so it would
        // never be found by the loop above - this is what misses brand-new
        // #1 launches. Two sources fill that gap, run in order:
        //
        //  1. DiscoverFromComicList - the real weekly shipping list from
        //     comiclist.info (see ComicListService). This is the primary,
        //     most comprehensive source when it's reachable: real titles,
        //     real prices, real variant covers, for the actual week. Any
        //     Series it creates gets added into allSeries here so step 2
        //     below sees them as already-known and doesn't duplicate them.
        //  2. DiscoverFromGcd - the Grand Comics Database's official API
        //     (comics.org), as a second, independent cross-check. Weaker
        //     per-entry data than ComicList (no exact ship date, and
        //     publisher name costs extra lookups - see GcdService), so it
        //     mainly catches anything ComicList's parse missed, same role
        //     as step 3 below.
        //  3. DiscoverNewNumberOnes - Comic Vine-based discovery (either
        //     targeted by Tracked Publishers or a global #1 scan). Runs
        //     regardless, both to catch anything the other two missed and
        //     as the sole source if they're ever both unreachable (every
        //     source here fails soft - null/empty, not an exception - so
        //     one breaking never breaks the others).
        var beforeComicList = result.NewIssuesAdded;
        DiscoverFromComicList(allSeries, result);
        trace.AppendLine($"ComicList: +{result.NewIssuesAdded - beforeComicList} issue(s).");

        var beforeGcd = result.NewIssuesAdded;
        DiscoverFromGcd(allSeries, result);
        trace.AppendLine($"GCD: +{result.NewIssuesAdded - beforeGcd} issue(s).");

        var beforeComicVine = result.NewIssuesAdded;
        DiscoverNewNumberOnes(allSeries, result);
        trace.AppendLine($"Comic Vine discovery: +{result.NewIssuesAdded - beforeComicVine} issue(s).");

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

    // Picks the best Comic Vine match for a series with no human in the
    // loop: an exact (case-insensitive) name match is required, and if more
    // than one volume shares that exact name, the one whose start year
    // matches the series wins; otherwise the first exact match is used. A
    // series with no confident name match is left unlinked and just skipped
    // this run instead of guessing - it's retried on the next check.
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
            return false;
        }

        var exact = matches.Where(m => string.Equals(m.Name, series.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0) return false;

        var best = exact.FirstOrDefault(m => m.StartYear == series.StartYear) ?? exact[0];

        series.ComicVineVolumeId = best.Id;
        result.SeriesAutoLinked++;
        return true;
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
            return;
        }

        var knownNumbers = series.Issues.Select(i => i.IssueNumber).ToHashSet();

        foreach (var cvIssue in cvIssues)
        {
            AddIssueIfNew(series, cvIssue, knownNumbers, result);
        }

        series.LastCheckedForNewIssues = DateTime.Now;
    }

    private static (DateOnly since, DateOnly until) DiscoveryWindow() =>
        (DateOnly.FromDateTime(DateTime.Today.AddMonths(-3)), DateOnly.FromDateTime(DateTime.Today.AddMonths(3)));

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
            entries = entries.Where(e => trackedPublishers.Any(p =>
                e.Publisher.Contains(p, StringComparison.OrdinalIgnoreCase)));
        }

        var groups = entries.GroupBy(e => (e.SeriesName!.Trim(), e.IssueNumber!.Value));

        var seriesByName = allSeries
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var (seriesName, issueNumber) = group.Key;

            if (!seriesByName.TryGetValue(seriesName, out var series))
            {
                series = new Series { Name = seriesName, StartYear = DateTime.Today.Year };
                _db.AllSeries.Add(series);
                seriesByName[seriesName] = series;
                allSeries.Add(series); // so later discovery steps see it as already-known
            }

            if (series.Issues.Any(i => i.IssueNumber == issueNumber)) continue; // already carried

            var covers = group.Select(e => new Cover
            {
                CoverLetter = e.CoverLetter,
                // Not a real UPC, same convention as Comic Vine-sourced
                // covers (see AddIssueIfNew) - the store sets the real
                // barcode once the book physically arrives (Inventory's
                // cover-detail grid).
                Barcode = $"{barcodePrefix}-{Guid.NewGuid():N}",
                Price = e.Price ?? 0,
                StockQuantity = 0
            }).ToList();

            var issue = new Issue
            {
                Series = series,
                IssueNumber = issueNumber,
                Publisher = group.First().Publisher,
                Type = IssueType.SingleIssue,
                ReleaseDate = releaseDate,
                // Neither source gives a real FOC date - same 3-week rule
                // of thumb used for Comic Vine-sourced issues.
                FocDate = releaseDate.AddDays(-21),
                Covers = covers
            };

            _db.Issues.Add(issue);

            result.NewIssuesAdded++;
            result.Added.Add($"{seriesName} #{issueNumber:0.##} ({covers.Count} cover(s), {releaseDate:MM/dd/yyyy})");
        }
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
