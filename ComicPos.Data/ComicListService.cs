using System.Text.RegularExpressions;
using ComicPos.Core.Models;
using ComicPos.Core.Services;
using HtmlAgilityPack;

namespace ComicPos.Data;

// Scrapes comiclist.info - a plain blog that republishes the real
// Lunar Distribution/Diamond-style comic shipping and solicitation data,
// grouped by publisher, with every title, variant cover, and US price.
// robots.txt there is wide open ("User-agent: *" with no Disallow lines),
// unlike comiclist.com (fully disallowed) and freshcomics.us (dead -
// every page but the stale cached homepage returned errors when checked).
// Two different kinds of post are scraped here, covering two different
// jobs (see IComicListService for which is which):
//   - the weekly "Chuck's Comic List" master post - what's physically
//     shipping THIS week (GetThisWeeksMasterList)
//   - per-publisher "Extended Forecast" posts - real solicited data
//     ~10 months out, the one that actually matters for FOC buying
//     (GetExtendedForecastEntries)
//
// This is HTML scraping against a site with no official API and no
// guarantee its markup stays the same, so every step here is written to
// fail LOUD instead of soft: if the site's layout changes, a request is
// blocked, or the parsed result doesn't look like a real list, these
// methods throw a specific, readable InvalidOperationException rather
// than quietly returning null/empty. AutoFetchService already catches
// whatever's thrown and reports it in the Check Now result, so a real
// problem shows up as a concrete line in that popup (and in
// checknow-debug.txt) instead of silently vanishing as "added 0" with
// nothing to go on.
public class ComicListService : IComicListService
{
    private const string IndexUrl = "https://comiclist.info/categories/this-week/";
    private const string ExtendedForecastIndexUrl = "https://comiclist.info/categories/extended-forecasts/";
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

    // Extended Forecast posts are per-publisher (one post for DC, one for
    // Marvel, one for Image, etc.), each covering that publisher's
    // solicited lineup for roughly the next 10 months - this is the data
    // that makes an FOC report actually useful (see IComicListService).
    // Fetches every current publisher's post, parses all of them, and
    // returns one combined list. A single publisher's post failing to
    // parse doesn't sink the whole thing - other publishers still get
    // reported - but if every post comes back empty, that's treated as a
    // real failure, same fail-loud contract as GetThisWeeksMasterList.
    public List<ComicListEntry> GetExtendedForecastEntries()
    {
        var urls = FindExtendedForecastUrls();
        if (urls.Count == 0)
            throw new InvalidOperationException("couldn't find any 'Extended Forecast' post links on comiclist.info/categories/extended-forecasts/ - site layout may have changed");

        var entries = new List<ComicListEntry>();
        var failures = new List<string>();

        // Kept purely for diagnostics if this comes back empty - the first
        // successfully-fetched post's raw HTML and extracted lines, so a
        // parsing failure is debuggable from checknow-debug.txt alone
        // instead of needing another blind guess at the page's real shape.
        string? sampleUrl = null;
        string? sampleHtml = null;
        List<string>? sampleLines = null;

        foreach (var url in urls)
        {
            try
            {
                var html = Http.GetStringAsync(url).GetAwaiter().GetResult();
                var lines = ExtractForecastTableLines(html);

                if (sampleHtml == null)
                {
                    sampleUrl = url;
                    sampleHtml = html;
                    sampleLines = lines;
                }

                entries.AddRange(ParseExtendedForecastEntries(lines));
            }
            catch (Exception ex)
            {
                failures.Add($"{url}: {ex.Message}");
            }
        }

        if (entries.Count < MinPlausibleEntries)
        {
            var detail = failures.Count > 0 ? $" Per-post failures: {string.Join(" | ", failures)}" : "";

            var sample = "";
            if (sampleHtml != null)
            {
                var lineSample = string.Join(" | ", (sampleLines ?? new List<string>()).Take(10).Select(l => l.Length > 100 ? l[..100] + "..." : l));
                var bodyStart = sampleHtml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
                var rawSnippet = bodyStart >= 0
                    ? sampleHtml.Substring(bodyStart, Math.Min(4000, sampleHtml.Length - bodyStart))
                    : sampleHtml[..Math.Min(4000, sampleHtml.Length)];
                sample = $"\nSample post: {sampleUrl}\nExtracted {sampleLines?.Count ?? 0} line(s): [{lineSample}]\n" +
                          $"--- raw HTML near <body> (first 4000 chars) ---\n{rawSnippet}";
            }

            throw new InvalidOperationException(
                $"parsed only {entries.Count} extended-forecast entries total across {urls.Count} publisher post(s) (expected {MinPlausibleEntries}+).{detail}{sample}");
        }

        return entries;
    }

    // The extended-forecasts index lists one (or more, across pages) post
    // per publisher, re-posted every week. Keeps only the newest URL per
    // publisher slug (the part of the URL before "-extended-forecast-for")
    // so an older re-post of the same publisher doesn't get double-counted.
    private static List<string> FindExtendedForecastUrls()
    {
        var html = Http.GetStringAsync(ExtendedForecastIndexUrl).GetAwaiter().GetResult();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var linkRegex = new Regex(
            @"https?://comiclist\.info/(\d{4})/(\d{2})/(\d{2})/(?<slug>[^""'\s/]*)-extended-forecast-for[^""'\s]*\.html",
            RegexOptions.IgnoreCase);

        var bestByPublisher = new Dictionary<string, (DateTime date, string url)>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = a.GetAttributeValue("href", "");
            var m = linkRegex.Match(href);
            if (!m.Success) continue;

            var postDate = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            var publisher = m.Groups["slug"].Value;

            if (!bestByPublisher.TryGetValue(publisher, out var existing) || postDate > existing.date)
                bestByPublisher[publisher] = (postDate, m.Value);
        }

        return bestByPublisher.Values.Select(v => v.url).ToList();
    }

    // Extended Forecast lines look like:
    //   "11/18/26 11/18/26 DC COMICS Absolute Flash #21 (Cover A Nick Robles)"
    // current-ship-date, original-ship-date, then the publisher/imprint in
    // ALL CAPS, then the title in normal Title Case - no price anywhere
    // (unlike the weekly master list), so this is a different line shape
    // entirely and needs its own parser. The imprint/title boundary has no
    // markup to lean on, so it's found the same way a person reading the
    // line would: imprint words are ALL CAPS (e.g. "COMICS", "DC",
    // "COMICS/VERTIGO"), title words have a lowercase letter in them (e.g.
    // "Absolute") - walk words until the first one that isn't all-caps,
    // everything before that is the imprint, everything from there on is
    // the title. "Cancelled"/"TBD" sections at the bottom of each post
    // don't start with two dates, so they're naturally skipped rather than
    // parsed as real solicitations.
    private static readonly Regex ForecastDatePair = new(
        @"^(?<date1>\d{1,2}/\d{1,2}/\d{2})\s+(?<date2>\d{1,2}/\d{1,2}/\d{2})\s+(?<rest>.+)$",
        RegexOptions.Compiled);

    private static bool IsAllCapsToken(string token)
    {
        var hasLetter = false;
        foreach (var c in token)
        {
            if (!char.IsLetter(c)) continue;
            hasLetter = true;
            if (char.IsLower(c)) return false;
        }
        return hasLetter;
    }

    // A token belongs to the imprint if it's all-caps-with-a-letter (the
    // normal case), OR it's a pure digit/punctuation token (no letters at
    // all) immediately followed by an all-caps word - that second case
    // exists for publishers like "247 COMICS", whose name itself starts
    // with a number. Without it, "247" would fail the all-caps test (no
    // letters means it can't be "all caps"), stop the imprint scan at
    // position 0, and silently drop the entire line. The look-ahead is
    // what tells "247 COMICS Some Title" apart from a title that happens
    // to start with a number, like "100 Bullets The US Of Anger #5" -
    // there, the word after "100" is "Bullets" (has a lowercase letter),
    // so the look-ahead correctly refuses to treat "100" as part of an
    // imprint and the scan stops immediately, right where it should.
    private static bool IsImprintToken(string[] words, int index)
    {
        var token = words[index];
        if (IsAllCapsToken(token)) return true;

        var hasDigit = token.Any(char.IsDigit);
        var hasLower = token.Any(char.IsLower);
        if (!hasDigit || hasLower) return false;

        return index + 1 < words.Length && IsAllCapsToken(words[index + 1]);
    }

    private static List<ComicListEntry> ParseExtendedForecastEntries(List<string> lines)
    {
        var entries = new List<ComicListEntry>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // "Cancelled"/"TBD" section headers stop this post's parsing
            // outright, in case entries underneath keep the same
            // date-prefixed shape (unconfirmed either way, so this is a
            // cheap safeguard rather than a known bug) - a cancelled or
            // not-yet-dated book has no business being added as upcoming.
            if (line.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("Cancelled:", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("TBD", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("TBD:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var dateMatch = ForecastDatePair.Match(line);
            if (!dateMatch.Success) continue; // header/column-title lines etc. - not a real entry

            if (!DateOnly.TryParse(dateMatch.Groups["date1"].Value, out var shipDate)) continue;

            var rest = dateMatch.Groups["rest"].Value.Trim();
            var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var i = 0;
            while (i < words.Length && IsImprintToken(words, i)) i++;

            if (i == 0 || i >= words.Length) continue; // no imprint found, or nothing left for a title

            var publisher = string.Join(' ', words.Take(i));
            var title = string.Join(' ', words.Skip(i));
            if (title.Length == 0) continue;

            var entry = new ComicListEntry
            {
                Publisher = publisher,
                RawTitle = title,
                ReleaseDate = shipDate
            };

            var issueMatch = IssueNumberPattern.Match(title);
            if (issueMatch.Success)
            {
                entry.IssueNumber = float.Parse(issueMatch.Groups[1].Value);
                entry.SeriesName = title[..issueMatch.Index].Trim().TrimEnd('-', ':', ',');
            }
            else
            {
                // Same "no # at all" case as the weekly parser - a TP/HC/
                // GN/one-shot solicitation, not dropped, just treated as
                // its own single-issue product (see ParseEntries below).
                entry.SeriesName = title;
                entry.IssueNumber = 1f;
            }

            var coverMatch = CoverLetterPattern.Match(title);
            if (coverMatch.Success)
                entry.CoverLetter = coverMatch.Groups[1].Value.ToUpperInvariant();

            entries.Add(entry);
        }

        return entries;
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

    // Extended Forecast posts lay their data out as a literal HTML
    // <table> (<TR><TD>date</TD><TD>date</TD><TD>imprint</TD><TD>title</TD></TR>),
    // not as one block element per entry like the weekly list's <li>/<p>
    // layout. ExtractTextLines breaks on BOTH <tr> and <td>, which is
    // right for the weekly list but wrong here - it would split each
    // row's date/date/imprint/title into four separate lines instead of
    // keeping them together on one. This only breaks on <tr>, so a row's
    // cells stay on one combined line, e.g. "11/18/26 11/18/26 IDW
    // PUBLISHING Star Trek (2026) #2 (Cover A Dennis Menheere)" - the
    // exact shape ForecastDatePair expects. (This is the real root cause
    // of the "parsed only 0 extended-forecast entries" failure: the dates
    // were ending up alone on their own lines, so no line ever matched
    // "date date rest" as a whole.)
    private static List<string> ExtractForecastTableLines(string html)
    {
        var text = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Row boundaries -> newline. Cells within a row are deliberately
        // left alone (just stripped below along with every other tag) so
        // a row's cells land on one line together.
        text = Regex.Replace(text, @"</?tr\b[^>]*>", "\n", RegexOptions.IgnoreCase);

        // Everything else (td, links, bold, spans, images, ...) just
        // stripped, keeping its text inline.
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
                else
                {
                    // No "#N" at all - this is a trade paperback, hardcover,
                    // graphic novel, art book, or other one-shot product
                    // (comiclist.info's list is comics AND book-trade
                    // product in one feed). These used to get silently
                    // dropped here entirely, which - across a list full of
                    // book-only imprints (Abrams ComicArts, Dark Horse
                    // Books, Dutton, Candlewick, Golden Books, Penguin
                    // Workshop, etc.) - was quietly throwing out a large
                    // chunk of the week's real list. Treat each one as its
                    // own single-issue "series" (issue #1) so it still
                    // makes it into inventory instead of vanishing.
                    entry.SeriesName = title;
                    entry.IssueNumber = 1f;
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
