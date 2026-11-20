using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComicPos.Core.Models;
using ComicPos.Core.Services;
using HtmlAgilityPack;

namespace ComicPos.Data;

// Scrapes comicade.com's public FOC catalogue
// (comicade.com/catalogue/foc/{yyyy-MM-dd}, one page per FOC week,
// Wednesdays). comicade.com is a Laravel + Inertia.js app, and Inertia's
// own protocol is what makes this worth building: a plain GET returns a
// full HTML page with the exact same data embedded as JSON in a
// <script type="application/json"> tag (for the page's initial render),
// but a GET carrying the headers "X-Inertia: true" and a matching
// "X-Inertia-Version" gets that identical JSON back directly, with no
// HTML at all - this is Inertia's documented client/server protocol, not
// an undocumented trick, and it means no HTML parsing is needed for
// anything past the very first request (DiscoverVersion below).
//
// What that JSON gives, per issue, is dramatically better than anything
// comiclist.info can offer: an exact foc_date and release_date (not a
// "release minus 3 weeks" guess), a real UPC, a real retail price, a
// direct cover image URL, and series title/issue number already split
// apart (no regex title-parsing needed at all). The trade-off is horizon:
// comicade.com's own solicited data currently only runs a couple of
// months out, versus comiclist.info's Extended Forecast posts (~10
// months) - see IComicadeFocService for how the two sources are meant to
// complement each other.
//
// Same fail-loud contract as ComicListService: a request that's blocked,
// a response that doesn't parse the way expected, or the very first week
// being unreachable all throw a specific, readable InvalidOperationException
// instead of quietly returning an empty list. A single later week failing
// on its own is recorded and skipped (see GetUpcomingFocEntries) since
// partial real data is still useful.
public class ComicadeFocService : IComicadeFocService
{
    private const string BaseUrl = "https://comicade.com/catalogue/foc/";
    private static readonly HttpClient Http = BuildClient();

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComicPos/1.0 (personal single-store inventory tool)");
        return client;
    }

    // "CVR A MIKEL JANIN" -> "A". comicade.com's cvr field is free text,
    // not a clean cover-letter field, so this is best-effort same as
    // ComicListService's CoverLetterPattern - a miss just leaves the
    // default "A" rather than losing the entry.
    private static readonly Regex CoverLetterInCvr = new(
        @"^CVR\s+([A-Za-z])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<ComicListEntry> GetUpcomingFocEntries()
    {
        var startWeek = NextWednesdayOnOrAfter(DateOnly.FromDateTime(DateTime.Today));

        string version;
        try
        {
            version = DiscoverVersion(startWeek);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"couldn't reach comicade.com's FOC catalogue for the week of {startWeek:yyyy-MM-dd} - {ex.Message}");
        }

        var entries = new List<ComicListEntry>();
        var failures = new List<string>();
        var consecutiveEmptyWeeks = 0;
        const int maxWeeks = 26; // safety cap (~6 months) - the real horizon today is much shorter than this

        var week = startWeek;
        for (var i = 0; i < maxWeeks && consecutiveEmptyWeeks < 2; i++, week = week.AddDays(7))
        {
            int total;
            try
            {
                using var doc = FetchWeekPage(week, 1, ref version);
                var issuesProp = doc.RootElement.GetProperty("props").GetProperty("issues");
                entries.AddRange(ParseIssuesArray(issuesProp.GetProperty("data")));

                total = issuesProp.GetProperty("total").GetInt32();
                var lastPage = issuesProp.GetProperty("last_page").GetInt32();

                for (var page = 2; page <= lastPage; page++)
                {
                    try
                    {
                        using var pageDoc = FetchWeekPage(week, page, ref version);
                        var pageData = pageDoc.RootElement.GetProperty("props").GetProperty("issues").GetProperty("data");
                        entries.AddRange(ParseIssuesArray(pageData));
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{week:yyyy-MM-dd} page {page}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{week:yyyy-MM-dd}: {ex.Message}");
                consecutiveEmptyWeeks++;
                continue;
            }

            consecutiveEmptyWeeks = total == 0 ? consecutiveEmptyWeeks + 1 : 0;
        }

        if (entries.Count == 0)
        {
            var detail = failures.Count > 0 ? $" Per-week failures: {string.Join(" | ", failures)}" : "";
            throw new InvalidOperationException(
                $"comicade.com's FOC catalogue returned no usable entries starting from the week of {startWeek:yyyy-MM-dd}.{detail}");
        }

        return entries;
    }

    // The Wednesday of this week if today IS Wednesday, otherwise the
    // next upcoming one - deliberately never a Wednesday that's already
    // passed, since this is meant for forward FOC decisions only.
    private static DateOnly NextWednesdayOnOrAfter(DateOnly date)
    {
        var diff = ((int)DayOfWeek.Wednesday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(diff);
    }

    // One plain, header-less GET - exactly what a browser's address bar
    // would do - purely to read the Inertia asset "version" string out of
    // the page's embedded <script type="application/json"> tag. Every
    // other request in this class skips HTML entirely (see FetchWeekPage).
    private static string DiscoverVersion(DateOnly week)
    {
        var url = $"{BaseUrl}{week:yyyy-MM-dd}";
        var html = Http.GetStringAsync(url).GetAwaiter().GetResult();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var scriptNode = doc.DocumentNode.SelectSingleNode("//script[@type='application/json']")
            ?? throw new InvalidOperationException($"no embedded page-data script found on {url} - site layout may have changed");

        using var pageDoc = JsonDocument.Parse(scriptNode.InnerText);
        return pageDoc.RootElement.TryGetProperty("version", out var versionEl) && versionEl.ValueKind == JsonValueKind.String
            ? versionEl.GetString()!
            : throw new InvalidOperationException($"embedded page data on {url} had no 'version' field - site layout may have changed");
    }

    // Fetches one page of one FOC week as Inertia's own JSON response
    // (see the class comment) - no HTML involved. If the asset version
    // has rotated since DiscoverVersion ran (comicade.com shipped a
    // deploy mid-run), the server answers with HTTP 409 instead of data;
    // this refreshes the version once and retries before giving up, and
    // updates the caller's copy (via ref) so every later call in the same
    // run uses the fresh version instead of hitting 409 repeatedly.
    private static JsonDocument FetchWeekPage(DateOnly week, int page, ref string version)
    {
        var url = $"{BaseUrl}{week:yyyy-MM-dd}" + (page > 1 ? $"?page={page}" : "");

        var json = FetchInertiaJson(url, version);
        if (json == null)
        {
            version = DiscoverVersion(week);
            json = FetchInertiaJson(url, version)
                ?? throw new InvalidOperationException($"comicade.com rejected {url} even after refreshing its page version");
        }

        return JsonDocument.Parse(json);
    }

    // Returns null specifically on HTTP 409 (Inertia's version-mismatch
    // response) so FetchWeekPage can refresh and retry; anything else
    // either succeeds or throws.
    private static string? FetchInertiaJson(string url, string version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Inertia", "true");
        request.Headers.Add("X-Inertia-Version", version);
        request.Headers.Add("Accept", "application/json");

        using var response = Http.SendAsync(request).GetAwaiter().GetResult();
        if ((int)response.StatusCode == 409) return null;

        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static List<ComicListEntry> ParseIssuesArray(JsonElement dataArray)
    {
        var result = new List<ComicListEntry>();
        foreach (var item in dataArray.EnumerateArray())
        {
            var entry = ParseOneIssue(item);
            if (entry != null) result.Add(entry);
        }
        return result;
    }

    private static ComicListEntry? ParseOneIssue(JsonElement item)
    {
        var seriesTitle = GetString(item, "series_title");
        if (string.IsNullOrWhiteSpace(seriesTitle)) return null;

        if (!item.TryGetProperty("number", out var numberEl)) return null;
        float? issueNumber = numberEl.ValueKind switch
        {
            JsonValueKind.Number => (float)numberEl.GetDouble(),
            JsonValueKind.String when float.TryParse(numberEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
        if (issueNumber == null) return null;

        var publisher = GetString(item, "series", "editorial", "name") ?? GetString(item, "editorial_slug") ?? "";

        var cvr = GetString(item, "cvr");
        var coverLetter = "A";
        if (!string.IsNullOrWhiteSpace(cvr))
        {
            var m = CoverLetterInCvr.Match(cvr);
            if (m.Success) coverLetter = m.Groups[1].Value.ToUpperInvariant();
        }

        decimal? price = null;
        var priceText = GetString(item, "retail_price");
        if (priceText != null && decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPrice))
            price = parsedPrice;

        var rawTitle = string.IsNullOrWhiteSpace(cvr)
            ? $"{seriesTitle} #{issueNumber:0.##}"
            : $"{seriesTitle} #{issueNumber:0.##} ({cvr})";

        return new ComicListEntry
        {
            Publisher = publisher,
            RawTitle = rawTitle,
            SeriesName = seriesTitle!.Trim(),
            IssueNumber = issueNumber,
            CoverLetter = coverLetter,
            Price = price,
            ReleaseDate = ParseIsoDate(item, "release_date"),
            FocDate = ParseIsoDate(item, "foc_date"),
            Upc = GetString(item, "upc"),
            CoverImageUrl = GetString(item, "thumbCover")
        };
    }

    private static DateOnly? ParseIsoDate(JsonElement item, string propName)
    {
        var text = GetString(item, propName);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? DateOnly.FromDateTime(dt)
            : null;
    }

    // Walks a dotted property path (e.g. "series","editorial","name")
    // through nested JSON objects, returning null at any missing step or
    // if the final value isn't a string - comicade.com's own JSON, so
    // this should always succeed, but a changed field shouldn't crash the
    // whole scrape, just drop that one piece of information.
    private static string? GetString(JsonElement obj, params string[] path)
    {
        var current = obj;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
