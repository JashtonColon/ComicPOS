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

    // New Cover rows created by BackfillCoverVariantsFromPrhBookPages for a
    // sibling cover-letter variant that had nothing on file at all before
    // (as opposed to CoversBackfilled above, which also covers upgrading
    // an EXISTING cover's placeholder/derived barcode to a real one that
    // method discovers - see its own doc comment). Kept separate so the
    // result popup can say "found N new cover(s), upgraded M barcode(s)"
    // instead of one undifferentiated number.
    public int CoverVariantsAdded;

    public List<string> Added { get; } = new();
    public List<string> Errors { get; } = new();

    // True unless this specific run's SaveChanges() call actually failed
    // (see AutoFetchService.TrySaveChanges) - added 2026-11-24 so a
    // caller that runs this same result-producing method many times in a
    // loop (WalkAndImportLunarDates, one call per week during a Lunar
    // backfill) can tell a real per-file failure apart from a normal
    // success, instead of just checking whether Errors is non-empty
    // (which can also be non-empty on an otherwise-successful run, e.g.
    // one row's cover image failing to save while everything else goes
    // through fine). Every OTHER AutoFetchResult-returning method leaves
    // this at its default (true) - it only ever gets set false by
    // ImportLunarDistributionRelease.
    public bool Saved = true;

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

    // Every issue already in the database (Comic Vine finds, CSV imports,
    // or hand-entered items alike) for the New Books tab - not a fresh API
    // call. Used to narrow this to a last-3-months/next-3-months window;
    // removed 2026-11-26 so the page's own "All Months" filter and month
    // tabs can actually reach every month in the database instead of
    // being capped before they ever see it.
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
    // limit caps how many candidate issues are processed (first N found) -
    // for the "Retry Metron Enrichment (Test 5)" button, so a change to
    // this method can be sanity-checked on a handful of issues in well
    // under a minute instead of committing to the full multi-hour run
    // blind (added 2026-11-23 after a full run came back 0/806 with no
    // way to tell why until FindIssueId's FailureReason existed either).
    // Null means no cap - the real, full run.
    AutoFetchResult RetryMetronEnrichment(Action<string>? onProgress = null, int? limit = null);

    // Same catch-up job as RetryMetronEnrichment above, sourced from LOCG
    // instead - added 2026-11-23 specifically because Metron itself is
    // IP/network-blocked right now (see metron-integration-notes.md), so
    // this is the one path that can still make progress on the backlog of
    // issues showing a placeholder Publisher/UPC/price while that's
    // sorted out. Matches both "MTR-" and "LOCG-" placeholder covers (see
    // AutoFetchService.RetryLocgEnrichment's own doc comment for why it's
    // not narrowly Metron-only). Same limit/Test-5-button contract as
    // RetryMetronEnrichment - null means the real, full run.
    AutoFetchResult RetryLocgEnrichment(Action<string>? onProgress = null, int? limit = null);

    // Hand-picked diagnostic button, added 2026-11-24 - hardcoded to two
    // specific issues (Absolute Wonder Woman #26, Absolute Flash #21),
    // checked against comicade.com's FOC catalogue specifically (NOT
    // LOCG) since that's the no-Cloudflare source that actually carries
    // real UPC data. Updates whatever's on file for these two regardless
    // of whether they currently show a placeholder - see
    // AutoFetchService.RetryComicadeEnrichmentForSpecificIssues's own doc
    // comment, including the important caveat that Comicade's catalogue
    // is forward-looking only (no historical/past-week data). Not a
    // general-purpose "enrich any issue by name" feature.
    AutoFetchResult RetryComicadeEnrichmentForSpecificIssues(Action<string>? onProgress = null);

    // Imports a Lunar Distribution weekly release .xlsx file (the kind
    // the store receives directly from its own distributor) - added
    // 2026-11-24 as a new, zero-network-risk discovery source: real UPC,
    // price, publisher, description, FOC date, street date, and an
    // embedded real cover photo for every row, none of it scraped or
    // rate-limited. See AutoFetchService.ImportLunarDistributionRelease's
    // own doc comment for the full rationale and how it reuses the same
    // AddEntriesAsIssues pipeline every other source feeds into. Called
    // once per uploaded file from NewBooks.razor's file picker - never
    // run automatically, since there's no way to know when the store has
    // this week's file in hand. Same onProgress contract as
    // BackfillYearFromLocg above (assume nothing about which thread calls
    // this on).
    AutoFetchResult ImportLunarDistributionRelease(string filePath, Action<string>? onProgress = null);

    // Imports PRH's "Comics Retail" solicitation spreadsheet - added
    // 2026-11-25 as a second zero-network-risk discovery source,
    // alongside Lunar above: a clean, already-tabular .xlsx (ISBN/UPC,
    // Title, Creators, Imprint, US/CAN retail price, Format, FOC Date,
    // On-Sale Date per row) covering PRH's FULL Direct Market
    // distribution list - Marvel, DC, Dark Horse, IDW, Oni Press,
    // Kodansha, Seven Seas, Titan Books and many more, not just Penguin
    // Random House's own imprints. See AutoFetchService.
    // ImportPrhCatalogRelease's own doc comment for the full rationale
    // and how it reuses the same AddEntriesAsIssues pipeline every other
    // source feeds into. No embedded cover photo in this feed (unlike
    // Lunar) - a newly-imported series/issue gets its cover the same way
    // any other source without one already does, via the existing Comic
    // Vine auto-link/backfill pass. Called once per uploaded file from
    // NewBooks.razor's file picker - same onProgress/threading contract
    // as every other method here.
    AutoFetchResult ImportPrhCatalogRelease(string filePath, Action<string>? onProgress = null);

    // Automated counterpart to ImportPrhCatalogRelease above - added
    // 2026-11-25/26 after confirming, in a real browser, exactly what
    // prhcomics.com's own "Download Excel Spreadsheet" button does: a
    // WordPress-AJAX-plus-nonce chain ending in a POST to a separate REST
    // microservice (sales-docgen.prh.com) that hands back the .xlsx,
    // base64-encoded - needs no PRH account/login at all (confirmed on a
    // totally anonymous page load). catalogCode is PRH's own "YYYY-MM"
    // solicitation-month code (e.g. "2026-12"); unlike Lunar's homepage,
    // PRH's catalog list links to several months at once rather than
    // exposing one single "current" one, so the caller supplies the
    // month it wants. See AutoFetchService.FetchPrhCatalogFromWebsite's
    // own doc comment and PrhWebsiteFetcher's class comment for the full
    // chain. Same onProgress/threading contract as every other method
    // here.
    AutoFetchResult FetchPrhCatalogFromWebsite(string catalogCode, Action<string>? onProgress = null);

    // Fetches every catalog prhcomics.com's own public archive currently
    // lists (confirmed live 2026-11-26 it currently runs 2021-06 through
    // whichever month is newest right now - 60+ catalogs), rather than
    // just the one month FetchPrhCatalogFromWebsite above covers - added
    // after the store asked for all available months, not just one at a
    // time. Discovers the real list from prhcomics.com's own archive
    // listing page instead of guessing a date range. See
    // AutoFetchService.FetchAllAvailablePrhCatalogsFromWebsite's own doc
    // comment for the full discovery mechanism. This is a genuinely
    // long-running operation (one full download+import per catalog), so
    // it's meant to be run by hand, never on a timer - same reasoning as
    // BackfillAllLunarHistory below.
    AutoFetchResult FetchAllAvailablePrhCatalogsFromWebsite(Action<string>? onProgress = null);

    // Automated counterpart to ImportLunarDistributionRelease above -
    // added 2026-11-24 after confirming lunardistribution.com's own
    // homepage is plain server-rendered HTML exposing the current
    // release/FOC dates, and that its file-download endpoint
    // (/home/productdatafile?release=<date> or ?foc=<date>) needs no
    // cookies/session/login at all. Reads the homepage to find today's
    // two current dates, downloads both .xlsx files itself (a plain
    // HttpClient GET - no browser, no Cloudflare), and feeds each one
    // through ImportLunarDistributionRelease. This is the "no manual
    // download+upload needed" path NewBooks.razor's automated button
    // calls - see AutoFetchService.FetchLunarReleaseFromWebsite's own doc
    // comment for why it's a one-click manual action rather than part of
    // the scheduled weekly check (the site only ever exposes one current
    // week at a time, so a timer would just redownload the same file).
    // Same onProgress/threading contract as every other method here.
    AutoFetchResult FetchLunarReleaseFromWebsite(Action<string>? onProgress = null);

    // Read-only - added 2026-11-24 after the Lunar title-parsing bug (see
    // AutoFetchService.DuplicateSeriesKey's own doc comment) was confirmed
    // to have split some already-existing series into two separate rows
    // ("Name" and "Name (2024)") the first time it ran for real. Scans
    // every Series for name collisions once a trailing "(YYYY)" is
    // stripped and reports what it finds - issue counts, external links -
    // without changing anything, so the actual merge below can be
    // reviewed first instead of run blind.
    AutoFetchResult FindDuplicateSeries();

    // The real fix for whatever FindDuplicateSeries above reports -
    // merges every duplicate group it would find onto whichever member
    // looks most established (most issues, then an external Comic
    // Vine/Metron link, then oldest Id), moving/merging issues and covers
    // and re-pointing any PullListEntry that pointed at a series being
    // removed. Changes the database for real - not reversible from
    // inside the app. See AutoFetchService.MergeDuplicateSeries's own doc
    // comment for exactly how a conflicting issue number is handled.
    AutoFetchResult MergeDuplicateSeries(Action<string>? onProgress = null);

    // Hand-picked one-time cleanup, added 2026-11-24 - hardcoded to the
    // exact two duplicate series found on the user's own real data that
    // night. See AutoFetchService.DeleteKnownDuplicateSeries's own doc
    // comment for exactly which rows and why this is a plain delete
    // rather than MergeDuplicateSeries above. Not a general-purpose
    // "delete any series" feature.
    AutoFetchResult DeleteKnownDuplicateSeries(Action<string>? onProgress = null);

    // One-time cleanup, added 2026-11-24 after the UPC/Publisher-not-saving
    // investigation (see AutoFetchService.AddMissingCoverVariants's own doc
    // comment) - the store asked to wipe any Lunar book that never got real
    // data so a fresh re-import starts clean. See
    // AutoFetchService.DeleteLunarPlaceholderCovers's own doc comment for
    // exactly what this can and can't identify: only covers that fell back
    // to a generated "LUN-" barcode (no real UPC ever came through) - a
    // Lunar cover that DID get a real UPC is indistinguishable from any
    // other source's and is already correctly saved, so it's left alone.
    // Confirm-gated and genuinely deletes - not reversible from inside the
    // app, same as DeleteKnownDuplicateSeries/MergeDuplicateSeries above.
    AutoFetchResult DeleteLunarPlaceholderCovers(Action<string>? onProgress = null);

    // Full historical backfill - added 2026-11-24 after the store asked
    // for Lunar history "as far back as it goes... then as far into the
    // future as possible", not just the single current week
    // FetchLunarReleaseFromWebsite above covers. Confirmed live that
    // lunardistribution.com's own date-nav arrows are backed by two small
    // JSON AJAX endpoints (POST /home/instoreproducts and
    // /home/focproducts) that report a "previous"/"next" date for real
    // catalog weeks, and a clean sentinel ("1/1/0001" backward, a
    // deliberately-far placeholder forward) once there's nothing more.
    // Walks backward through release history and forward through FOC
    // solicitations using the site's own chain (not a guessed weekly
    // cadence - confirmed live that real dates don't always land exactly
    // 7 days apart), downloading and importing every week that has real
    // products via the existing ImportLunarDistributionRelease pipeline.
    // See AutoFetchService.BackfillAllLunarHistory's own doc comment for
    // the full investigation, including how it recovers from the one rare
    // dead/gap date confirmed in the server's own chain. Can mean dozens
    // of file downloads in one run, so like BackfillYearFromLocg/
    // CheckForNewIssuesViaMetron this reports progress once per week
    // rather than only at the end - same onProgress/threading contract as
    // every other method here. Never run automatically; meant to be a
    // one-time catch-up (or an occasional re-run to pick up newly
    // published future weeks), kicked off by hand from NewBooks.razor.
    AutoFetchResult BackfillAllLunarHistory(Action<string>? onProgress = null);

    // Added 2026-11-27 - see AutoFetchService.FetchAllHistoryBothDistributors's
    // own doc comment for the full reasoning: chains BackfillAllLunarHistory,
    // then FetchAllAvailablePrhCatalogsFromWebsite, then
    // BackfillCoverVariantsFromPrhBookPages, in that fixed order, into one
    // combined result - the store's "one button that gets both [Lunar and
    // PRH], all the way back" request.
    AutoFetchResult FetchAllHistoryBothDistributors(Action<string>? onProgress = null);

    // Dev/demo seed data - added 2026-11-24, the store asked for fake
    // customers, a fake sales history, and (added later the same day)
    // fake pull-list subscriptions, all built from the real (now
    // correctly-saved) inventory, so Dashboard/History/Customers/FOC
    // Report/"Who's Pulling This?" all have something realistic to show
    // while the rest of the app is still being built out. See
    // AutoFetchService.GenerateFakeCustomersAndSalesHistory's own doc
    // comment for exactly what counts as "real" here (any non-placeholder
    // cover currently in inventory, not literally only Lunar's - there's
    // no column that records which source added a given row) and why
    // it's safe to click more than once (existing customers and
    // subscriptions aren't duplicated; sales are always added fresh).
    // Decrements real stock for each fake sale, the same way a real
    // checkout would - but unlike a real checkout, never lets it go
    // negative: an item with no stock yet gets a small simulated receiving
    // bump first, so this never puts a never-received book into negative
    // stock (which would also make it wrongly show up in the Inventory
    // page's now-received-only view). Never run automatically.
    AutoFetchResult GenerateFakeCustomersAndSalesHistory(int customerCount = 15, int saleCount = 60, Action<string>? onProgress = null);

    // Retroactive catch-up pass, added 2026-11-24 after confirming
    // ComicVineService's two issue-fetching methods had always pulled Comic
    // Vine's smaller "medium_url" image variant, not a display-quality one
    // (see ComicVineService.GetBestImageUrl's own doc comment) - root
    // causing persistent pixelation on every Comic-Vine-sourced cover
    // already saved before today's fix, independent of any CSS/JS display-
    // side scaling (the New Books detail modal's own cover card is a
    // completely different element from the zoom-overlay, which already
    // got its own separate fix earlier). Checks every cover's actual saved
    // image dimensions on disk (ComicPos.Core.ImageDimensionReader, no
    // image-processing dependency needed) rather than trusting a source
    // column that doesn't exist, and for anything narrower than the low-res
    // threshold, asks Comic Vine for a fresh, higher-resolution copy of
    // EXACTLY that series+issue (ComicVineService.FindBestCoverImageUrl).
    //
    // Not limited to covers that were already Comic-Vine-linked (fixed the
    // same day, right after this first shipped): a low-res cover from a
    // source that embeds its OWN low-res photo directly - confirmed live,
    // this is most of them, since the Lunar .xlsx importer embeds its own
    // cover photo and never touches Comic Vine at all - gets a real,
    // conservative attempt to auto-link its Series to Comic Vine first
    // (the SAME exact-name-match logic AutoFetchService.TryAutoLink already
    // uses for the normal weekly discovery flow, including its 24h retry
    // cooldown for a title Comic Vine will never carry), before giving up.
    // A series Comic Vine doesn't carry at all, or whose exact issue number
    // isn't found there once linked, is still left completely untouched -
    // same conservative, never-guess spirit as RescanExistingBooks/
    // RetryMetronEnrichment either way. limit caps how many candidates get
    // processed in one run, same Test-N-button contract as
    // RetryMetronEnrichment above. Never run automatically.
    AutoFetchResult UpgradeLowResolutionCovers(Action<string>? onProgress = null, int? limit = null);

    // Added 2026-11-25/26, right after Bug #15's Comic-Vine-auto-link fix
    // shipped for "lots of covers missing" - the user pushed back ("ok but
    // the pdfs do have covers"), correctly pointing out that PRH's own PDF
    // catalog clearly shows real cover art, which doesn't square with "PRH's
    // feed never includes a cover image." Investigated live and found the
    // real source: prhcomics.com's own catalog pages (and the product grid
    // that renders on them) load every cover thumbnail from a plain,
    // public, unauthenticated image CDN keyed directly by the item's own
    // barcode - https://images.penguinrandomhouse.com/cover/{barcode} -
    // completely independent of the PDF/xls docgen chain and needing no
    // Comic Vine match, no auto-link, no guessing at all. See
    // AutoFetchService.BackfillCoversFromPrhImageCdn's own doc comment for
    // the full story (both barcode shapes it answers, and how a "nothing
    // on file yet" response is told apart from a real cover via the shared
    // PlaceholderImageRegistry). limit caps how many candidates get
    // processed in one run, same Test-N-button contract as
    // RetryMetronEnrichment/UpgradeLowResolutionCovers above. Never run
    // automatically.
    AutoFetchResult BackfillCoversFromPrhImageCdn(Action<string>? onProgress = null, int? limit = null);

    // Added 2026-11-26, same day - a DIFFERENT PRH mechanism from
    // BackfillCoversFromPrhImageCdn above, found after the store pointed
    // out a real-world miss (Amazing Spider-Man #35 showing only "Cover A"
    // in the app despite genuinely having several variant covers) and then
    // showed exactly where PRH keeps that data: not the combined "Comics
    // Retail" feed/PDF this app already imports (confirmed live, by
    // direct inspection of a real monthly file, that feed only ever lists
    // ONE row per issue - always "Cover A" - so Bug #16's shared-barcode
    // logic has nothing to act on from it), but prhcomics.com's own
    // per-product page. Confirmed live: https://prhcomics.com/book/?isbn=
    // {anyCoverOfAnIssue'sOwnBarcode} is a plain, public, server-rendered,
    // cookie-less page (fetch()'d with credentials:'omit' and it still
    // works) whose "Additional covers" carousel already lists every OTHER
    // cover of that same issue - each with its own genuinely distinct,
    // real barcode (not a shared UPC, and not this app's own derived
    // "{barcode}#{letter}" fallback), a title string in PRH's familiar
    // "SERIES #N COVER X ARTIST VARIANT" grammar (same one
    // PrhTitleParser already parses for the catalog importer - reused
    // as-is here), and a price - confirmed even for a cover with no
    // final artwork yet (an incentive "Cover D TBD Artist Virgin 1:100
    // Variant" row already had its real barcode assigned). The store's
    // own caveat still applies: not every variant PRH eventually
    // solicits is necessarily listed here yet, and this says nothing
    // about non-PRH-distributed exclusives (a con-exclusive foil, say) -
    // it only ever ADDS/upgrades what PRH itself has on file, never
    // removes anything. See AutoFetchService.
    // BackfillCoverVariantsFromPrhBookPages's own doc comment for exactly
    // which issues qualify as candidates and how a real barcode replaces
    // an existing placeholder/derived one. Same Test-N-button contract as
    // RetryMetronEnrichment/BackfillCoversFromPrhImageCdn above. Never run
    // automatically.
    AutoFetchResult BackfillCoverVariantsFromPrhBookPages(Action<string>? onProgress = null, int? limit = null);
}
