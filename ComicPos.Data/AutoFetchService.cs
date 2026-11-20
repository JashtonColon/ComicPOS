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
    private readonly IStoreSettingsService _storeSettings;

    public AutoFetchService(ComicDbContext db, IComicVineService comicVine, IComicListService comicList, IComicadeFocService comicadeFoc, IGcdService gcd, IStoreSettingsService storeSettings)
    {
        _db = db;
        _comicVine = comicVine;
        _comicList = comicList;
        _comicadeFoc = comicadeFoc;
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

        var skippedCooldown = 0;
        var skippedAutoLinkCooldown = 0;
        foreach (var series in allSeries)
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
            else if (series.LastCheckedForNewIssues != null &&
                     DateTime.Now - series.LastCheckedForNewIssues.Value < RecheckCooldown)
            {
                skippedCooldown++;
                continue; // checked too recently to be worth another Comic Vine call
            }

            CheckOneSeries(series, result);
        }
        if (skippedAutoLinkCooldown > 0)
            trace.AppendLine($"  ({skippedAutoLinkCooldown} unlinked series skipped - auto-link attempted within the last {AutoLinkRetryCooldown.TotalHours:0}h and didn't match, e.g. TPs/GNs that can never match a Comic Vine volume.)");
        trace.AppendLine($"  done - linked {result.SeriesAutoLinked}, {result.NewIssuesAdded} issue(s) so far from existing series" +
            (skippedCooldown > 0 ? $" ({skippedCooldown} skipped - checked within the last {RecheckCooldown.TotalHours:0}h)." : "."));

        // allSeries only covers books the store already carries. A series
        // that has never been carried has no row here at all, so it would
        // never be found by the loop above - this is what misses brand-new
        // #1 launches and future solicitations. Three sources fill that
        // gap, run in order:
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
        // Stamped on every attempt - success, no-match, or exception alike
        // (see AutoLinkRetryCooldown above) - so a series that doesn't
        // match isn't retried again until the cooldown passes.
        series.LastAutoLinkAttempt = DateTime.Now;

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

        foreach (var cover in missingCovers)
            cover.CoverImagePath = imagePath;
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

            // A plain LINQ projection won't do here - downloading a cover
            // image (comicade.com entries only) is a side effect, and a
            // failed download for one variant shouldn't lose the others.
            var covers = new List<Cover>();
            foreach (var e in group)
            {
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

                covers.Add(new Cover
                {
                    CoverLetter = e.CoverLetter,
                    // A real UPC when the source gives one (comicade.com) -
                    // the same generated placeholder as every other source
                    // otherwise. The store sets the real barcode once the
                    // book physically arrives either way (Inventory's
                    // cover-detail grid), so this only matters until then.
                    Barcode = !string.IsNullOrWhiteSpace(e.Upc) ? e.Upc : $"{barcodePrefix}-{Guid.NewGuid():N}",
                    Price = e.Price ?? 0,
                    StockQuantity = 0,
                    CoverImagePath = coverImagePath
                });
            }

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
                Type = IssueType.SingleIssue,
                ReleaseDate = entryReleaseDate,
                FocDate = entryFocDate,
                Covers = covers
            };

            _db.Issues.Add(issue);

            result.NewIssuesAdded++;
            result.Added.Add($"{seriesName} #{issueNumber:0.##} ({covers.Count} cover(s), {entryReleaseDate:MM/dd/yyyy})");
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
