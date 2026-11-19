using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.Data;

public class GcdService : IGcdService
{
    private const string BaseUrl = "https://www.comics.org/api";

    // Pagination/call-budget guards - GCD rate-limits anonymous access
    // (the docs don't say by how much), and resolving a publisher name
    // costs two extra calls per distinct series (issue -> series ->
    // publisher). Capping how many pages/series we touch keeps one run
    // from hammering a free, unauthenticated API too hard.
    private const int MaxPages = 5;
    private const int MaxSeriesToResolve = 150;
    private static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(400);

    private static readonly HttpClient Http = BuildClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Publisher names rarely change - caching across the process's
    // lifetime avoids re-resolving the same series/publisher on every
    // weekly check.
    private static readonly Dictionary<string, string> SeriesPublisherUrlCache = new();
    private static readonly Dictionary<string, string> PublisherNameCache = new();

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComicPos/1.0 (personal single-store inventory tool)");
        // comics.org's API is Django REST Framework, which by default
        // renders its "browsable API" - an actual HTML page, not JSON -
        // unless the request explicitly asks for JSON. Without this
        // header every call here silently got HTML back instead of data
        // (surfaced as a JSON parse error starting with '<').
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // See IGcdService - deliberately does NOT swallow its own failures.
    public ComicListWeek GetThisWeeksOnSaleIssues()
    {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var year = ISOWeek.GetYear(today.ToDateTime(TimeOnly.MinValue));
            var week = ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue));

            var issues = FetchOnSaleIssues(year, week);
            if (issues.Count == 0)
                throw new InvalidOperationException($"on_sale_weekly returned 0 issues for ISO week {week}/{year}");

            var distinctSeriesUrls = issues.Select(i => i.series).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct().Take(MaxSeriesToResolve).ToList();
            foreach (var seriesUrl in distinctSeriesUrls)
                ResolvePublisherName(seriesUrl!);

            var entries = new List<ComicListEntry>();
            foreach (var issue in issues)
            {
                if (issue.variant_of != null) continue; // one row per base issue, not per cover variant
                if (issue.price != null && !issue.price.Contains("USD", StringComparison.OrdinalIgnoreCase)) continue;

                var seriesName = Regex.Replace(issue.series_name ?? "", @"\s*\(\d{4}(?:-\d{4})?\s+series\)\s*$", "", RegexOptions.IgnoreCase).Trim();
                if (seriesName.Length == 0) continue;

                var numberMatch = Regex.Match(issue.descriptor ?? "", @"^\s*(\d+(?:\.\d+)?)");
                if (!numberMatch.Success) continue; // no plain issue number (TPs, omnibuses, etc.) - skip rather than guess

                decimal? price = null;
                if (issue.price != null)
                {
                    var priceMatch = Regex.Match(issue.price, @"(\d+(?:\.\d+)?)");
                    if (priceMatch.Success) price = decimal.Parse(priceMatch.Value, CultureInfo.InvariantCulture);
                }

                var publisher = "";
                if (issue.series != null &&
                    SeriesPublisherUrlCache.TryGetValue(issue.series, out var pubUrl) &&
                    PublisherNameCache.TryGetValue(pubUrl, out var resolvedName))
                {
                    publisher = resolvedName;
                }

                entries.Add(new ComicListEntry
                {
                    Publisher = publisher,
                    RawTitle = $"{seriesName} #{issue.descriptor}",
                    SeriesName = seriesName,
                    IssueNumber = float.Parse(numberMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                    CoverLetter = "A",
                    Price = price
                });
            }

            if (entries.Count == 0)
                throw new InvalidOperationException($"fetched {issues.Count} issues from on_sale_weekly but none had a plain issue number to parse");

            // GCD only gives a month, not an exact day - estimate the
            // Wednesday of the requested ISO week as the ship date (every
            // major US publisher ships new comics on Wednesday).
            var releaseDate = ISOWeek.ToDateTime(year, week, DayOfWeek.Wednesday);

            return new ComicListWeek { ReleaseDate = DateOnly.FromDateTime(releaseDate), Entries = entries };
    }

    private static List<GcdIssueDto> FetchOnSaleIssues(int year, int week)
    {
        var all = new List<GcdIssueDto>();
        string? url = $"{BaseUrl}/issue/on_sale_weekly/{year}/week/{week}/";

        var pages = 0;
        while (url != null && pages < MaxPages)
        {
            Thread.Sleep(RequestDelay);
            var json = Http.GetStringAsync(url).GetAwaiter().GetResult();
            var page = JsonSerializer.Deserialize<GcdIssueListResponse>(json, JsonOptions);
            if (page?.results == null) break;

            all.AddRange(page.results);
            url = page.next;
            pages++;
        }

        return all;
    }

    private static void ResolvePublisherName(string seriesUrl)
    {
        if (SeriesPublisherUrlCache.ContainsKey(seriesUrl)) return;

        try
        {
            Thread.Sleep(RequestDelay);
            var seriesJson = Http.GetStringAsync(seriesUrl).GetAwaiter().GetResult();
            var series = JsonSerializer.Deserialize<GcdSeriesDto>(seriesJson, JsonOptions);
            if (series?.publisher == null) return;

            SeriesPublisherUrlCache[seriesUrl] = series.publisher;

            if (PublisherNameCache.ContainsKey(series.publisher)) return;

            Thread.Sleep(RequestDelay);
            var pubJson = Http.GetStringAsync(series.publisher).GetAwaiter().GetResult();
            var publisher = JsonSerializer.Deserialize<GcdPublisherDto>(pubJson, JsonOptions);
            if (!string.IsNullOrWhiteSpace(publisher?.name))
                PublisherNameCache[series.publisher] = publisher.name;
        }
        catch
        {
            // Leave this series unresolved - its entries just end up with
            // a blank Publisher, which Tracked Publishers filtering (in
            // AutoFetchService) naturally excludes rather than mishandles.
        }
    }

    private class GcdIssueListResponse
    {
        public int count { get; set; }
        public string? next { get; set; }
        public List<GcdIssueDto>? results { get; set; }
    }

    private class GcdIssueDto
    {
        public string? series_name { get; set; }
        public string? descriptor { get; set; }
        public string? publication_date { get; set; }
        public string? price { get; set; }
        public string? variant_of { get; set; }
        public string? series { get; set; }
    }

    private class GcdSeriesDto
    {
        public string? publisher { get; set; }
    }

    private class GcdPublisherDto
    {
        public string? name { get; set; }
    }
}
