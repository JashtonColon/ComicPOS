using System.Globalization;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComicPos.Core;
using ComicPos.Core.Models;
using ComicPos.Core.Services;
using HtmlAgilityPack;
using Microsoft.Playwright;

namespace ComicPos.Data;

// See ILocgService for the overall "why Playwright, why not part of the
// normal weekly check" rationale. This class is the Cloudflare-handling
// browser session plus the HTML-fragment parsing; AutoFetchService owns
// looping weeks/years and the crawl-delay between calls.
//
// How the AJAX endpoint and its date format were found (no public docs
// for either): driving the site's own date picker in a real browser and
// watching the resulting network requests. Two things that looked
// reasonable both turned out wrong - /comics/new-comics/2026-01-07
// (dashes) 404s, and ?date=2026-01-07 as a query param is silently
// ignored - the real picker instead (a) normalizes whatever day you pick
// to that week's MONDAY and (b) actually drives a background AJAX call to
// /comic/get_comics?...&date_type=week&date=<MM/DD/YYYY>&... rather than
// a page navigation at all. That endpoint, with per_page set high enough,
// returns an entire week's results - cover image, publisher, title
// (including any variant name), release date, and price - in one
// response, as a JSON envelope whose "list" field is an HTML fragment of
// <li class="issue"> elements, one per issue/variant.
public class LocgService : ILocgService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;

    public List<ComicListEntry> GetWeekEntries(DateOnly mondayOfWeek)
    {
        // Playwright's API is async-only; everything else in this
        // codebase's service layer is synchronous (see
        // IComicListService/IGcdService), so this is the one place that
        // bridges the two - callers get the same plain synchronous
        // contract as every other discovery source.
        return GetWeekEntriesAsync(mondayOfWeek).GetAwaiter().GetResult();
    }

    private async Task<List<ComicListEntry>> GetWeekEntriesAsync(DateOnly mondayOfWeek)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureBrowserLaunchedAsync();
            var json = await FetchWeekJsonWithRetryAsync(mondayOfWeek);
            return ParseWeekResponse(json, mondayOfWeek);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void EnrichIssueDetails(ComicListEntry entry)
    {
        EnrichIssueDetailsAsync(entry).GetAwaiter().GetResult();
    }

    // LOCG's robots.txt crawl-delay (30s) - the same constraint
    // AutoFetchService's week-to-week loop already honors for the
    // weekly list page. Applied here, not left to the caller, since this
    // is called once per brand-new issue/cover deep inside
    // AutoFetchService.BuildCoverFromEntry's per-entry loop - that
    // caller shouldn't need to know LOCG's own pacing rule to get it
    // right.
    private static readonly TimeSpan LocgDetailPageCrawlDelay = TimeSpan.FromSeconds(30);

    private async Task EnrichIssueDetailsAsync(ComicListEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DetailUrl)) return;

        await _gate.WaitAsync();
        try
        {
            await EnsureBrowserLaunchedAsync();

            // A normal server-rendered page, not the special AJAX
            // endpoint that turned out to have its own harsh ~5-minute
            // quota (see FetchWeekJsonWithRetryAsync) - never actually
            // confirmed live whether this endpoint shares that same
            // limit, so this still retries defensively on a failure, but
            // with a much lighter budget than a whole week's list: this
            // is enrichment, called once per issue, not the one
            // make-or-break fetch for an entire week.
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await NavigateAndWaitForCloudflareAsync(entry.DetailUrl);
                    var html = await _page!.ContentAsync();
                    ParseIssueDetailPage(html, entry);
                    return;
                }
                catch when (attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Best-effort only (see ILocgService.EnrichIssueDetails) -
                    // a failure here leaves entry.Upc/FocDate unset rather
                    // than failing the whole issue/week import.
                    return;
                }
            }
        }
        finally
        {
            // Paid even on failure/early return - whatever just happened
            // still counted as a request against LOCG's crawl-delay.
            await Task.Delay(LocgDetailPageCrawlDelay);
            _gate.Release();
        }
    }

    private static readonly Regex FocDateTextPattern = new(@"^([A-Za-z]+)\s+(\d{1,2})", RegexOptions.Compiled);

    private static void ParseIssueDetailPage(string html, ComicListEntry entry)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Confirmed live (see ILocgService.EnrichIssueDetails): each
        // field on this page - UPC, Distributor SKU, Final Order
        // Cutoff, Cover Date - is its own
        // "details-addtl-block" div containing a "name" div (the label)
        // and a "value" div (the actual text). Matched on the class
        // shape rather than any more specific selector, same defensive
        // approach as the weekly-list parsing above.
        var blocks = doc.DocumentNode.SelectNodes(
            "//div[contains(concat(' ', normalize-space(@class), ' '), ' details-addtl-block ')]");
        if (blocks == null) return;

        foreach (var block in blocks)
        {
            var name = block.SelectSingleNode(".//div[contains(@class,'name')]")?.InnerText?.Trim();
            var value = block.SelectSingleNode(".//div[contains(@class,'value')]")?.InnerText?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) continue;

            if (string.Equals(name, "UPC", StringComparison.OrdinalIgnoreCase))
            {
                entry.Upc = value;
            }
            else if (name.Contains("Final Order Cutoff", StringComparison.OrdinalIgnoreCase))
            {
                // "Oct 26th" - a month name, a day, and an ordinal suffix,
                // but critically NO YEAR at all, so DateOnly.TryParse on
                // this text alone can't work. entry.ReleaseDate is
                // already known (from the weekly list, parsed before this
                // enrichment ever runs) and FOC always falls shortly
                // before the street date, so that's used to pick the
                // right year rather than guessing "this year" blindly -
                // which would be wrong for all of January's solicitations
                // (an FOC date in the preceding December).
                var focMatch = FocDateTextPattern.Match(value);
                if (focMatch.Success && int.TryParse(focMatch.Groups[2].Value, out var day))
                {
                    var monthNames = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames;
                    var monthIndex = Array.FindIndex(monthNames, 0, 12,
                        m => string.Equals(m, focMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                    if (monthIndex < 0)
                    {
                        var fullMonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;
                        monthIndex = Array.FindIndex(fullMonthNames, 0, 12,
                            m => string.Equals(m, focMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                    }

                    if (monthIndex >= 0)
                    {
                        var year = entry.ReleaseDate?.Year ?? DateTime.Today.Year;
                        DateOnly? candidate = null;
                        try { candidate = new DateOnly(year, monthIndex + 1, day); }
                        catch { /* e.g. Feb 30 from a misread day - leave FocDate unset */ }

                        if (candidate != null)
                        {
                            if (entry.ReleaseDate != null && candidate.Value > entry.ReleaseDate.Value)
                                candidate = candidate.Value.AddYears(-1);
                            entry.FocDate = candidate;
                        }
                    }
                }
            }
        }
    }

    // Confirmed live across two real runs: a fresh page navigation before
    // every fetch (see NavigateToWeekPageAsync) stops it being "works
    // once, then 403 forever" - but it's still only actually letting a
    // request through on exactly every 8th attempt, evenly spaced, every
    // single time, across the whole year (02/16, 04/13, 06/08, 08/03,
    // 09/28, 11/23 - each exactly 8 weeks after the last, no variance).
    // That's not occasional bad luck or a soft rate limit that eases up -
    // it's a hard, evenly-spaced quota window (roughly one request every
    // ~4-5 minutes given how long each attempt+30s-delay cycle takes).
    // Guessing that window's exact length and hard-coding a matching
    // delay would be fragile if it ever shifts, so instead this retries
    // specifically on a 403 (never on any other failure - a genuinely
    // different problem, like a layout change, isn't something retrying
    // fixes), with a real page navigation on every attempt same as any
    // other call, until it lands inside an open window or genuinely runs
    // out of attempts. 12 attempts x 60s is a 12-minute ceiling per week -
    // comfortably past the observed ~5-minute cycle, so a week should
    // rarely if ever actually exhaust this.
    private const int MaxAttemptsPerWeek = 12;
    private static readonly TimeSpan LocgRetryBackoff = TimeSpan.FromSeconds(60);

    private async Task<string> FetchWeekJsonWithRetryAsync(DateOnly mondayOfWeek)
    {
        for (var attempt = 1; attempt <= MaxAttemptsPerWeek; attempt++)
        {
            await NavigateToWeekPageAsync(mondayOfWeek);
            var url = BuildAjaxUrl(mondayOfWeek);

            try
            {
                // An in-page fetch(), not a separate HTTP client - this
                // automatically carries whatever cookies the session has
                // AND the current page's own URL as Referer, the same
                // way the site's own date picker and filters call this
                // exact endpoint from that same per-week page.
                return await _page!.EvaluateAsync<string>(
                    @"async (u) => {
                        const r = await fetch(u, { credentials: 'include' });
                        if (!r.ok) throw new Error('HTTP ' + r.status);
                        return await r.text();
                    }", url);
            }
            catch (Exception ex) when (ex.Message.Contains("HTTP 403") && attempt < MaxAttemptsPerWeek)
            {
                // Still inside a blocked window - wait and let the next
                // iteration's fresh navigation try again.
                await Task.Delay(LocgRetryBackoff);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"LOCG request for week of {mondayOfWeek:MM/dd/yyyy} failed: {ex.Message}", ex);
            }
        }

        // Unreachable in practice (every loop iteration above either
        // returns or throws before falling out the bottom), but the
        // compiler can't prove that from a runtime condition - this
        // satisfies "every code path returns or throws".
        throw new InvalidOperationException(
            $"LOCG request for week of {mondayOfWeek:MM/dd/yyyy} failed: still getting HTTP 403 after {MaxAttemptsPerWeek} attempts.");
    }

    // Launches the browser/page once per app lifetime - no navigation
    // here, since every call site now navigates itself (see
    // NavigateToWeekPageAsync) and this is just "make sure there's a
    // page to navigate".
    private async Task EnsureBrowserLaunchedAsync()
    {
        if (_context != null) return;

        _playwright = await Playwright.CreateAsync();

        // A dedicated profile, separate from the person's real Chrome
        // profile and from any other Playwright use in this app -
        // whatever cookies/local-storage Cloudflare leaves behind to
        // remember a passed challenge persist here across app
        // restarts, so a later run tomorrow doesn't necessarily have to
        // clear the challenge again from scratch.
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ComicPos", "LocgBrowserProfile");
        Directory.CreateDirectory(profileDir);

        _context = await _playwright.Chromium.LaunchPersistentContextAsync(profileDir, new BrowserTypeLaunchPersistentContextOptions
        {
            // Reuses the person's actual installed Chrome instead of
            // downloading Playwright's own ~300MB Chromium build.
            Channel = "chrome",
            // Deliberately NOT headless - Cloudflare's interactive
            // challenge occasionally needs an actual human click (the
            // same tradeoff a sibling project of this user's already
            // confirmed is sometimes unavoidable against this exact
            // kind of protection), and that's only possible if the
            // window is real and visible.
            Headless = false,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });
        _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
    }

    // Navigates to this week's own dated page - a REAL page load, not
    // just the AJAX call - every time this is called, not only on the
    // first one (see the comment on GetWeekEntriesAsync for why). LOCG's
    // own weekly pages live at /comics/new-comics/YYYY/MM/DD, normalized
    // to that week's Monday (confirmed by driving the site's own date
    // picker and watching which URL it navigated to).
    private async Task NavigateToWeekPageAsync(DateOnly mondayOfWeek)
    {
        var pageUrl = $"https://leagueofcomicgeeks.com/comics/new-comics/{mondayOfWeek.Year}/{mondayOfWeek.Month:D2}/{mondayOfWeek.Day:D2}";
        await NavigateAndWaitForCloudflareAsync(pageUrl);
    }

    // Shared by every real page navigation this class does (the weekly
    // page, and now each issue's own detail page for
    // EnrichIssueDetails) - Cloudflare's interstitial can in principle
    // show up on any page load, not just the first one of a session, so
    // this is checked every time rather than only once at startup.
    private async Task NavigateAndWaitForCloudflareAsync(string url)
    {
        await _page!.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000
        });

        // Cloudflare's own interstitial replaces the page's <title> while
        // its challenge is running ("Just a moment...", "Checking your
        // browser..."). Polling for that to clear - rather than a fixed
        // sleep - is what gives a person time to actually click through
        // an interactive check if one ever appears, without making every
        // ordinary page load (no challenge shown at all) wait the same
        // fixed delay regardless.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var title = await _page.TitleAsync();
            if (!title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("checking your browser", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            await Task.Delay(1000);
        }
    }

    // facets[]=variant_types:0 through :8 and per_page=50000 were both
    // copied verbatim from a real request the site's own UI made (the
    // former just mirrors every "include this variant type" checkbox the
    // filter panel defaults to; the latter is what makes one request
    // return an entire week instead of paginating) - trimming either
    // down looked plausible but wasn't actually verified against a
    // request the real site makes on its own, so they're left exactly as
    // observed rather than guessed down to what "should" be the minimum.
    private static string BuildAjaxUrl(DateOnly mondayOfWeek)
    {
        var dateStr = mondayOfWeek.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
        var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var sb = new StringBuilder("https://leagueofcomicgeeks.com/comic/get_comics")
            .Append("?addons=1&list=releases&list_mode_offset=0&list_option=&list_filter=&list_refinement=")
            .Append("&user_id=0&view=thumbs&order=pulls")
            .Append("&format[]=1&format[]=6&format[]=2&media_format=")
            .Append("&date_type=week")
            .Append("&date=").Append(Uri.EscapeDataString(dateStr))
            .Append("&date_end=")
            .Append("&series_id=0&character=");

        for (var i = 0; i <= 8; i++)
            sb.Append("&facets[]=").Append(Uri.EscapeDataString($"variant_types:{i}"));

        sb.Append("&per_page=50000");
        sb.Append("&_=").Append(cacheBuster);
        return sb.ToString();
    }

    private static List<ComicListEntry> ParseWeekResponse(string json, DateOnly mondayOfWeek)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            // The single most likely reason this isn't valid JSON at all
            // is that Cloudflare intercepted the request and handed back
            // an HTML challenge page instead of the real endpoint's
            // response - worth saying directly rather than just "invalid
            // JSON", since the fix (clear the challenge, not a code fix)
            // is completely different.
            throw new InvalidOperationException(
                $"LOCG response for week of {mondayOfWeek:MM/dd/yyyy} wasn't valid JSON - likely a Cloudflare challenge page instead of real data: {ex.Message}");
        }

        if (!root.TryGetProperty("list", out var listProp) || listProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"LOCG response for week of {mondayOfWeek:MM/dd/yyyy} had no \"list\" field - site layout may have changed.");
        }

        var html = listProp.GetString() ?? "";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var nodes = doc.DocumentNode.SelectNodes("//li[contains(concat(' ', normalize-space(@class), ' '), ' issue ')]");
        var entries = new List<ComicListEntry>();
        if (nodes == null) return entries;

        foreach (var li in nodes)
        {
            var entry = ParseIssueNode(li);
            if (entry != null) entries.Add(entry);
        }

        return entries;
    }

    private static readonly Regex IssueNumberPattern = new(@"#(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex PricePattern = new(@"\$(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static ComicListEntry? ParseIssueNode(HtmlNode li)
    {
        var publisher = li.SelectSingleNode(".//div[contains(@class,'publisher')]")?.InnerText?.Trim() ?? "";

        var titleNode = li.SelectSingleNode(".//div[contains(@class,'title')]");
        if (titleNode == null) return null;

        var variantNode = titleNode.SelectSingleNode(".//span[contains(@class,'variant-name')]");
        var variantText = variantNode?.InnerText == null ? null : HtmlEntity.DeEntitize(variantNode.InnerText).Trim();

        // The variant-name span is nested INSIDE the title div for a
        // variant cover's row but absent entirely for the base/default-
        // cover row - strip it out before parsing so a variant and its
        // own base issue produce the IDENTICAL series-name+issue-number
        // text. That's what lets AddEntriesAsIssues' own (SeriesName,
        // IssueNumber) grouping merge them into one Issue with several
        // Covers, the same way it already does for comiclist.info's
        // "(Cover B ...)" title suffix - no separate need to track LOCG's
        // own data-parent id for this.
        var coreTitleHtml = titleNode.InnerHtml;
        if (variantNode != null)
            coreTitleHtml = coreTitleHtml.Replace(variantNode.OuterHtml, "");
        var coreTitle = HtmlEntity.DeEntitize(Regex.Replace(coreTitleHtml, "<[^>]+>", " "));
        coreTitle = Regex.Replace(coreTitle, @"\s+", " ").Trim();

        var entry = new ComicListEntry
        {
            Publisher = publisher,
            RawTitle = variantText != null ? $"{coreTitle} ({variantText})" : coreTitle
        };

        var issueMatch = IssueNumberPattern.Match(coreTitle);
        if (issueMatch.Success)
        {
            entry.IssueNumber = float.Parse(issueMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            entry.SeriesName = coreTitle[..issueMatch.Index].Trim().TrimEnd('-', ':', ',');
        }
        // No "#" at all (collected editions, non-numbered one-shots) -
        // SeriesName/IssueNumber stay null, same as ComicListService does
        // for the same shape of title. AddEntriesAsIssues already filters
        // those out rather than guessing at an issue number.

        if (!string.IsNullOrWhiteSpace(variantText))
        {
            entry.VariantLabel = variantText;
            // A variant whose name doesn't have "Cover <letter>" anywhere
            // in it (an incentive ratio variant named only by its artist,
            // say - "Ryan Sook Variant") has no real letter to read at
            // all. Used to fall back to a single hardcoded "B" here,
            // which meant every such variant on the same issue collided
            // on that one literal letter - confirmed live: Ultimates #19
            // showed three/four different variants all labeled Cover B,
            // each save silently overwriting the slot the last one took.
            // "B" is still written here as a provisional placeholder
            // (every ComicListEntry needs *a* non-null CoverLetter), but
            // CoverLetterIsGuess=true tells AutoFetchService.
            // ResolveCoverLetters this one's letter isn't real and needs
            // its own free slot assigned, not treated as a confident claim
            // on "B".
            var guessed = CoverLetterParser.GuessFromName(variantText);
            entry.CoverLetter = guessed ?? "B";
            entry.CoverLetterIsGuess = guessed == null;
        }

        var dateAttr = li.SelectSingleNode(".//span[contains(@class,'date')]")?.GetAttributeValue("data-date", "");
        if (!string.IsNullOrWhiteSpace(dateAttr) && long.TryParse(dateAttr, out var unixSeconds))
            entry.ReleaseDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);

        var priceText = li.SelectSingleNode(".//span[contains(@class,'price')]")?.InnerText?.Trim();
        if (!string.IsNullOrWhiteSpace(priceText))
        {
            var priceMatch = PricePattern.Match(priceText);
            if (priceMatch.Success)
                entry.Price = decimal.Parse(priceMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            else
                entry.PriceIsAskRetailer = true; // "AR"/"Ask Your Retailer" - no $ figure to parse
        }

        var imgNode = li.SelectSingleNode(".//img");
        var imgSrc = imgNode?.GetAttributeValue("data-src", "");
        if (string.IsNullOrWhiteSpace(imgSrc))
            imgSrc = imgNode?.GetAttributeValue("src", "");
        if (!string.IsNullOrWhiteSpace(imgSrc))
            entry.CoverImageUrl = imgSrc;

        // The per-issue detail page (UPC, real FOC date - neither is on
        // this weekly list at all, see EnrichIssueDetails) - whatever the
        // FIRST link pointing at "/comic/" inside this <li> is, same
        // defensive "look for the shape, not one exact class name"
        // approach as everything else in this file, since the exact
        // wrapper markup around that link was never directly inspected.
        var linkHref = li.SelectSingleNode(".//a[contains(@href,'/comic/')]")?.GetAttributeValue("href", "");
        if (!string.IsNullOrWhiteSpace(linkHref))
        {
            entry.DetailUrl = linkHref.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? linkHref
                : $"https://leagueofcomicgeeks.com{(linkHref.StartsWith('/') ? "" : "/")}{linkHref}";
        }

        return entry;
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _context?.CloseAsync().GetAwaiter().GetResult();
            _playwright?.Dispose();
        }
        catch
        {
            // Best-effort cleanup on app shutdown - nothing left to
            // report a failure here to.
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
