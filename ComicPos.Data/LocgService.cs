using System.Globalization;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        await _page!.GotoAsync(pageUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000
        });

        // Cloudflare's own interstitial replaces the page's <title> while
        // its challenge is running ("Just a moment...", "Checking your
        // browser..."). Polling for that to clear - rather than a fixed
        // sleep - is what gives a person time to actually click through
        // an interactive check if one ever appears, without making every
        // ordinary week (no challenge shown at all) wait the same fixed
        // delay regardless. Checked on every week's navigation, not just
        // the first, since there's no guarantee Cloudflare only ever
        // challenges the very first page load of a session.
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
    private static readonly Regex VariantCoverLetterPattern = new(@"^Cover\s+([A-Za-z])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
            var letterMatch = VariantCoverLetterPattern.Match(variantText);
            // A variant whose name doesn't start with "Cover <letter>"
            // (an incentive ratio variant named only by its artist, say)
            // still needs a letter distinct from the default "A" so it
            // doesn't collide with the base cover under
            // AddMissingCoverVariants' known-letters check - "B" is a
            // reasonable single fallback since LOCG's own grouping here
            // is already per-issue, not per-cover-letter.
            entry.CoverLetter = letterMatch.Success ? letterMatch.Groups[1].Value.ToUpperInvariant() : "B";
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
