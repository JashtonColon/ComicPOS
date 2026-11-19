using System.Text.RegularExpressions;
using ComicPos.Core.Models;
using ComicPos.Core.Services;
using HtmlAgilityPack;

namespace ComicPos.Data;

// Scrapes comiclist.info's weekly "Chuck's Comic List" master post - a
// plain blog that republishes the real weekly shipping list (compiled from
// Lunar Distribution/Diamond-style sources), grouped by publisher, with
// every title, variant cover, and US price. robots.txt there is wide open
// ("User-agent: *" with no Disallow lines), unlike comiclist.com (fully
// disallowed) and freshcomics.us (dead - every page but the stale cached
// homepage returned errors when checked).
//
// This is HTML scraping against a site with no official API and no
// guarantee its markup stays the same, so every step here is written to
// fail soft: if the site's layout changes, a request is blocked, or the
// parsed result doesn't look like a real list, GetThisWeeksMasterList()
// returns null and AutoFetchService just falls back to its existing Comic
// Vine-based discovery instead of polluting inventory with garbage.
public class ComicListService : IComicListService
{
    private const string IndexUrl = "https://comiclist.info/categories/this-week/";
    private static readonly HttpClient Http = BuildClient();

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComicPos/1.0 (personal single-store inventory tool)");
        return client;
    }

    // Below this many parsed entries, something's clearly wrong (blocked
    // request returning an error/placeholder page, site redesign, etc.) -
    // a real weekly list runs in the hundreds. Treat it as a parse failure
    // rather than feeding a near-empty or garbled list into inventory.
    private const int MinPlausibleEntries = 20;

    // Deliberately does NOT swallow its own failures - every soft-fail
    // point below throws a specific, readable message instead of quietly
    // returning null. AutoFetchService already catches whatever this
    // throws and reports it in the Check Now result (see
    // DiscoverFromComicList), so a parsing/layout problem shows up as a
    // concrete line in that popup instead of silently vanishing as
    // "added 0" with nothing to go on.
    public ComicListWeek GetThisWeeksMasterList()
    {
        var postUrl = FindLatestMasterListUrl()
            ?? throw new InvalidOperationException("couldn't find a 'Chuck's Comic List' master post link on the index page (comiclist.info/categories/this-week/) - site layout may have changed");

        var html = Http.GetStringAsync(postUrl).GetAwaiter().GetResult();
        var releaseDate = ParseReleaseDate(html, postUrl);
        var lines = ExtractTextLines(html);
        var entries = ParseEntries(lines);

        if (entries.Count < MinPlausibleEntries)
        {
            // Enough here to actually diagnose from the debug log alone
            // (see DebugLog/AutoFetchService), rather than guessing again:
            // how much HTML came back, how many lines got extracted, a
            // sample of those lines, AND a chunk of the raw HTML itself -
            // specifically to tell apart "the real list just isn't in the
            // static HTML at all (likely loaded by JavaScript after the
            // page loads, which a plain HTTP request can never see)" from
            // "the content's there but something about its shape still
            // isn't being split into lines right."
            var sample = string.Join(" | ", lines.Take(3).Select(l => l.Length > 80 ? l[..80] + "..." : l));
            var bodyStart = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            var rawSnippet = bodyStart >= 0
                ? html.Substring(bodyStart, Math.Min(4000, html.Length - bodyStart))
                : html[..Math.Min(4000, html.Length)];
            throw new InvalidOperationException(
                $"parsed only {entries.Count} entries from {postUrl} (expected {MinPlausibleEntries}+) - " +
                $"fetched {html.Length} chars of HTML, extracted {lines.Count} line(s). Sample lines: [{sample}]\n" +
                $"--- raw HTML near <body> (first 4000 chars) ---\n{rawSnippet}");
        }

        return new ComicListWeek { ReleaseDate = releaseDate, Entries = entries };
    }

    // The index page lists recent posts across several categories (per-
    // publisher posts plus the one combined "Chuck's Comic List for ..."
    // master post). Picks the master post with the most recent date in
    // its own URL (every comiclist.info post URL starts with /YYYY/MM/DD/).
    private static string? FindLatestMasterListUrl()
    {
        var html = Http.GetStringAsync(IndexUrl).GetAwaiter().GetResult();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var linkRegex = new Regex(
            @"https?://comiclist\.info/(\d{4})/(\d{2})/(\d{2})/[^""'\s]*chucks?-comic-list-for[^""'\s]*\.html",
            RegexOptions.IgnoreCase);

        string? best = null;
        DateTime bestDate = DateTime.MinValue;

        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = a.GetAttributeValue("href", "");
            var m = linkRegex.Match(href);
            if (!m.Success) continue;

            var postDate = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            if (postDate > bestDate)
            {
                bestDate = postDate;
                best = m.Value;
            }
        }

        return best;
    }

    // Prefers a date spelled out in the page itself ("Chuck's Comic List
    // for November 18, 2026" - the actual street date) over the post's
    // own publish-date URL, which is usually a couple of days earlier.
    // Works on the raw HTML string directly (not a parsed DOM) - see
    // ExtractTextLines below for why.
    private static DateOnly ParseReleaseDate(string html, string postUrl)
    {
        var m = Regex.Match(html, @"for\s+([A-Za-z]+)\s+(\d{1,2}),?\s+(\d{4})");
        if (m.Success && DateOnly.TryParse($"{m.Groups[1].Value} {m.Groups[2].Value}, {m.Groups[3].Value}", out var parsed))
            return parsed;

        var urlMatch = Regex.Match(postUrl, @"/(\d{4})/(\d{2})/(\d{2})/");
        if (urlMatch.Success)
            return new DateOnly(int.Parse(urlMatch.Groups[1].Value), int.Parse(urlMatch.Groups[2].Value), int.Parse(urlMatch.Groups[3].Value));

        return DateOnly.FromDateTime(DateTime.Today);
    }

    // Works directly on the raw HTML string with regex instead of walking
    // HtmlAgilityPack's parsed DOM. The DOM-walking version of this (an
    // earlier attempt) returned zero lines against this site's actual
    // master-list post even though the real content was plainly sitting
    // in the raw HTML - something about that specific page's markup
    // apparently trips up the parser/tree-walk, even though the SAME
    // library parses the index page fine. Regex against the literal
    // string can't be thrown off by that: block-level tag boundaries
    // become newlines, inline tags (a, strong, span, img, etc.) are just
    // stripped so a hyperlinked title and its price stay on one line, and
    // everything else left over is one final pass.
    private static List<string> ExtractTextLines(string html)
    {
        var text = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Block-level boundaries -> newline.
        text = Regex.Replace(text, @"</?(p|li|h[1-6]|div|tr|td|br|header|footer|article|ul|ol)\b[^>]*>", "\n", RegexOptions.IgnoreCase);

        // Everything else (links, bold, spans, images, ...) just stripped,
        // keeping its text inline.
        text = Regex.Replace(text, @"<[^>]+>", "");

        text = System.Net.WebUtility.HtmlDecode(text);

        return text.Split('\n')
            .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }


    // "Title text ..., $4.99" / "Title text ..., AR" - matched against the
    // LAST comma on the line (titles themselves sometimes contain commas,
    // e.g. "Godzilla Conquers The Multiverse #5 (Of 5), $4.99" is fine, but
    // a greedy match before the final comma handles rarer multi-comma
    // titles too).
    private static readonly Regex PriceLine = new(
        @"^(?<title>.+),\s*(?:\$(?<price>\d+(?:\.\d{2})?)|(?<ar>AR))\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A standalone publisher heading: short, no lowercase-only words that
    // would suggest body text, no price punctuation. Best-effort - if this
    // misses an occasional heading, entries just keep the previous
    // publisher attributed, which is a minor cosmetic miss, not a lost
    // entry (the title/price itself, which is what matters for inventory,
    // still gets parsed correctly either way).
    private static readonly Regex HeadingLine = new(
        @"^[A-Z0-9][A-Z0-9 .,'&/!-]{1,39}$", RegexOptions.Compiled);

    private static readonly Regex IssueNumberPattern = new(@"#(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex CoverLetterPattern = new(@"\(Cover\s+([A-Za-z])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<ComicListEntry> ParseEntries(List<string> lines)
    {
        var entries = new List<ComicListEntry>();
        var currentPublisher = "";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var priceMatch = PriceLine.Match(line);
            if (priceMatch.Success)
            {
                var title = priceMatch.Groups["title"].Value.Trim();
                if (title.Length == 0) continue;

                var entry = new ComicListEntry
                {
                    Publisher = currentPublisher,
                    RawTitle = title,
                    PriceIsAskRetailer = priceMatch.Groups["ar"].Success
                };

                if (priceMatch.Groups["price"].Success &&
                    decimal.TryParse(priceMatch.Groups["price"].Value, out var price))
                {
                    entry.Price = price;
                }

                var issueMatch = IssueNumberPattern.Match(title);
                if (issueMatch.Success)
                {
                    entry.IssueNumber = float.Parse(issueMatch.Groups[1].Value);
                    entry.SeriesName = title[..issueMatch.Index].Trim().TrimEnd('-', ':', ',');
                }

                var coverMatch = CoverLetterPattern.Match(title);
                if (coverMatch.Success)
                    entry.CoverLetter = coverMatch.Groups[1].Value.ToUpperInvariant();

                entries.Add(entry);
                continue;
            }

            if (line.Length <= 40 && HeadingLine.IsMatch(line) && !line.Contains('$'))
                currentPublisher = line;
        }

        return entries;
    }
}
