using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using ComicPos.Core.Services;

namespace ComicPos.Data;

// Talks to the free Comic Vine API (comicvine.gamespot.com/api/) to look up
// series ("volumes") and their upcoming issues. Comic Vine has no concept of
// FOC/solicitation dates - only street (store) dates - so FOC is estimated
// by the caller (AutoFetchService), not here.
public class ComicVineService : IComicVineService
{
    private static readonly HttpClient Http = BuildClient();
    private readonly IStoreSettingsService _storeSettings;

    // A weekly check can fire off dozens to hundreds of requests in one run
    // (one per series, plus discovery). Comic Vine's free tier rate-limits
    // bursts like that and returns HTTP 420 when it does. These two fields
    // throttle every request to a steady pace and retry with backoff on a
    // 420 instead of just failing that series/volume outright.
    private static readonly object ThrottleLock = new();
    private static DateTime _lastRequestAtUtc = DateTime.MinValue;
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(750);

    public ComicVineService(IStoreSettingsService storeSettings)
    {
        _storeSettings = storeSettings;
    }

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        // Comic Vine rejects requests with no/blank User-Agent.
        client.DefaultRequestHeaders.Add("User-Agent", "ComicPos/1.0 (independent comic shop point-of-sale)");
        return client;
    }

    // Every Comic Vine call (JSON or image) goes through here so the pacing
    // and rate-limit retry logic lives in exactly one place.
    private static void Throttle()
    {
        lock (ThrottleLock)
        {
            var wait = _lastRequestAtUtc + MinRequestInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            _lastRequestAtUtc = DateTime.UtcNow;
        }
    }

    // GETs a URL and returns the body as text, pacing requests and retrying
    // with backoff on HTTP 420 (Comic Vine's rate-limit response) instead
    // of failing the first time a burst of requests trips it.
    private static string FetchString(string url)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Throttle();
            using var response = Http.GetAsync(url).GetAwaiter().GetResult();

            if ((int)response.StatusCode == 420) // Comic Vine: rate limit exceeded
            {
                if (attempt == maxAttempts)
                    throw new InvalidOperationException("Comic Vine rate limit hit too many times in a row - try again in a few minutes.");
                Thread.Sleep(TimeSpan.FromSeconds(attempt * 5)); // 5s, 10s, 15s backoff
                continue;
            }

            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        throw new InvalidOperationException("Comic Vine request failed."); // unreachable - loop always returns or throws
    }

    private static byte[] FetchBytes(string url)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Throttle();
            using var response = Http.GetAsync(url).GetAwaiter().GetResult();

            if ((int)response.StatusCode == 420)
            {
                if (attempt == maxAttempts)
                    throw new InvalidOperationException("Comic Vine rate limit hit too many times in a row - try again in a few minutes.");
                Thread.Sleep(TimeSpan.FromSeconds(attempt * 5));
                continue;
            }

            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }

        throw new InvalidOperationException("Comic Vine request failed.");
    }

    public List<ComicVineVolumeMatch> SearchVolumes(string seriesName)
    {
        var apiKey = RequireApiKey();
        var url = "https://comicvine.gamespot.com/api/volumes/" +
                   $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                   $"&filter=name:{Uri.EscapeDataString(seriesName)}" +
                   "&field_list=id,name,start_year,publisher,count_of_issues&limit=15";

        var json = FetchString(url);
        using var doc = JsonDocument.Parse(json);

        var results = new List<ComicVineVolumeMatch>();
        if (!doc.RootElement.TryGetProperty("results", out var resultsEl)) return results;

        foreach (var item in resultsEl.EnumerateArray())
        {
            var match = new ComicVineVolumeMatch
            {
                Id = item.GetProperty("id").GetInt32(),
                Name = GetString(item, "name"),
                IssueCount = item.TryGetProperty("count_of_issues", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0
            };

            if (item.TryGetProperty("start_year", out var sy) && sy.ValueKind == JsonValueKind.String && int.TryParse(sy.GetString(), out var year))
                match.StartYear = year;

            if (item.TryGetProperty("publisher", out var pub) && pub.ValueKind == JsonValueKind.Object)
                match.Publisher = GetString(pub, "name");

            results.Add(match);
        }

        return results;
    }

    public List<ComicVineNewIssue> GetIssuesInRange(int volumeId, DateOnly sinceStoreDate, DateOnly untilStoreDate)
    {
        var apiKey = RequireApiKey();
        var filter = $"volume:{volumeId},store_date:{sinceStoreDate:yyyy-MM-dd}|{untilStoreDate:yyyy-MM-dd}";
        var url = "https://comicvine.gamespot.com/api/issues/" +
                   $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                   $"&filter={Uri.EscapeDataString(filter)}" +
                   "&field_list=issue_number,name,store_date,image&sort=store_date:asc&limit=100";

        var json = FetchString(url);
        using var doc = JsonDocument.Parse(json);

        var results = new List<ComicVineNewIssue>();
        if (!doc.RootElement.TryGetProperty("results", out var resultsEl)) return results;

        foreach (var item in resultsEl.EnumerateArray())
        {
            var issue = new ComicVineNewIssue { Name = GetString(item, "name") };

            if (item.TryGetProperty("issue_number", out var num))
            {
                var numStr = num.ValueKind == JsonValueKind.String ? num.GetString() : num.ToString();
                float.TryParse(numStr, out var parsed);
                issue.IssueNumber = parsed;
            }

            if (item.TryGetProperty("store_date", out var sd) && sd.ValueKind == JsonValueKind.String && DateOnly.TryParse(sd.GetString(), out var date))
                issue.StoreDate = date;

            if (item.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object && img.TryGetProperty("medium_url", out var mu))
                issue.ImageUrl = mu.GetString();

            results.Add(issue);
        }

        return results;
    }

    // Every #1 issue in the date window, across every volume/publisher -
    // this is how brand-new series launches are found, since a series the
    // store has never carried has no local Series row to search against.
    // Comic Vine caps a single page at 100 results, so this pages through
    // up to 300 (3 pages) to cover a busy window without hammering the API.
    public List<ComicVineNewIssue> GetNewNumberOneIssues(DateOnly sinceStoreDate, DateOnly untilStoreDate)
    {
        var apiKey = RequireApiKey();
        var filter = $"issue_number:1,store_date:{sinceStoreDate:yyyy-MM-dd}|{untilStoreDate:yyyy-MM-dd}";

        var results = new List<ComicVineNewIssue>();
        for (var offset = 0; offset < 300; offset += 100)
        {
            var url = "https://comicvine.gamespot.com/api/issues/" +
                       $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                       $"&filter={Uri.EscapeDataString(filter)}" +
                       "&field_list=issue_number,name,store_date,image,volume" +
                       $"&sort=store_date:asc&limit=100&offset={offset}";

            var json = FetchString(url);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
                break;

            var pageCount = 0;
            foreach (var item in resultsEl.EnumerateArray())
            {
                pageCount++;
                var issue = new ComicVineNewIssue { Name = GetString(item, "name") };

                if (item.TryGetProperty("issue_number", out var num))
                {
                    var numStr = num.ValueKind == JsonValueKind.String ? num.GetString() : num.ToString();
                    float.TryParse(numStr, out var parsed);
                    issue.IssueNumber = parsed;
                }

                if (item.TryGetProperty("store_date", out var sd) && sd.ValueKind == JsonValueKind.String && DateOnly.TryParse(sd.GetString(), out var date))
                    issue.StoreDate = date;

                if (item.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object && img.TryGetProperty("medium_url", out var mu))
                    issue.ImageUrl = mu.GetString();

                if (item.TryGetProperty("volume", out var vol) && vol.ValueKind == JsonValueKind.Object)
                {
                    if (vol.TryGetProperty("id", out var vid) && vid.ValueKind == JsonValueKind.Number)
                        issue.VolumeId = vid.GetInt32();
                    issue.VolumeName = GetString(vol, "name");
                }

                results.Add(issue);
            }

            if (pageCount < 100) break; // last page
        }

        return results;
    }

    // Single-volume lookup by id, used after GetNewNumberOneIssues to find a
    // candidate's publisher/start year before deciding whether to add it.
    // Comic Vine's single-resource volume endpoint needs the "4050-" volume
    // resource-type prefix on the id (its one well-known API quirk).
    public ComicVineVolumeDetails? GetVolumeDetails(int volumeId)
    {
        var apiKey = RequireApiKey();
        var url = $"https://comicvine.gamespot.com/api/volume/4050-{volumeId}/" +
                   $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                   "&field_list=id,name,start_year,publisher";

        var json = FetchString(url);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var res) || res.ValueKind != JsonValueKind.Object)
            return null;

        var details = new ComicVineVolumeDetails
        {
            Id = res.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : volumeId,
            Name = GetString(res, "name")
        };

        if (res.TryGetProperty("start_year", out var sy) && sy.ValueKind == JsonValueKind.String && int.TryParse(sy.GetString(), out var year))
            details.StartYear = year;

        if (res.TryGetProperty("publisher", out var pub) && pub.ValueKind == JsonValueKind.Object)
            details.Publisher = GetString(pub, "name");

        return details;
    }

    // Resolves a publisher name to its Comic Vine id. Shared by every
    // publisher-scoped lookup so the resolution logic (and its exact-name
    // match rule) lives in one place.
    private static int? ResolvePublisherId(string apiKey, string publisherName)
    {
        var url = "https://comicvine.gamespot.com/api/publishers/" +
                   $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                   $"&filter={Uri.EscapeDataString($"name:{publisherName}")}" +
                   "&field_list=id,name&limit=5";

        var json = FetchString(url);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var p in results.EnumerateArray())
        {
            if (string.Equals(GetString(p, "name"), publisherName, StringComparison.OrdinalIgnoreCase) &&
                p.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number)
                return pid.GetInt32();
        }
        return null;
    }

    // Resolves a publisher name to its Comic Vine id, then lists that
    // publisher's volumes sorted by most-recently-updated first. "Recently
    // updated" is Comic Vine's own activity signal - it ticks up whenever
    // a volume gets a new issue catalogued - so this surfaces both brand-
    // new launches AND long-running ongoing titles (Batman, Amazing
    // Spider-Man, etc. update constantly and sort right to the top) without
    // needing to know in advance which titles are "new" vs. "ongoing".
    // This is how "everything a tracked publisher currently puts out" is
    // found, instead of scanning every issue on Comic Vine, which is
    // overwhelmingly manga/foreign/small-press noise for a typical US shop.
    public List<ComicVineVolumeMatch> GetActiveVolumesByPublisher(string publisherName, int limit)
    {
        var apiKey = RequireApiKey();

        var publisherId = ResolvePublisherId(apiKey, publisherName);
        if (publisherId == null) return new List<ComicVineVolumeMatch>(); // publisher name didn't resolve - nothing to search

        var volumeUrl = "https://comicvine.gamespot.com/api/volumes/" +
                         $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                         $"&filter={Uri.EscapeDataString($"publisher:{publisherId}")}" +
                         $"&sort=date_last_updated:desc&limit={limit}" +
                         "&field_list=id,name,start_year,publisher,count_of_issues";

        var volumeJson = FetchString(volumeUrl);
        using var volumeDoc = JsonDocument.Parse(volumeJson);

        var results = new List<ComicVineVolumeMatch>();
        if (!volumeDoc.RootElement.TryGetProperty("results", out var volumeResults) || volumeResults.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in volumeResults.EnumerateArray())
        {
            var match = new ComicVineVolumeMatch
            {
                Id = item.GetProperty("id").GetInt32(),
                Name = GetString(item, "name"),
                Publisher = publisherName,
                IssueCount = item.TryGetProperty("count_of_issues", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0
            };

            if (item.TryGetProperty("start_year", out var sy) && sy.ValueKind == JsonValueKind.String && int.TryParse(sy.GetString(), out var year))
                match.StartYear = year;

            results.Add(match);
        }

        return results;
    }

    public string? DownloadCoverImage(string imageUrl, string fileNameHint)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos", "CoverImages");
            Directory.CreateDirectory(folder);

            var ext = Path.GetExtension(new Uri(imageUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

            var invalid = Path.GetInvalidFileNameChars();
            var safeName = new string(fileNameHint.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            var path = Path.Combine(folder, $"{safeName}{ext}");

            var bytes = FetchBytes(imageUrl);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private string RequireApiKey()
    {
        var key = _storeSettings.Load().ComicVineApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Comic Vine API key is set. Go to Settings and add one (free from comicvine.gamespot.com/api/).");
        return key;
    }
}
