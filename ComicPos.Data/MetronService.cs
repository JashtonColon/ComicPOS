using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.Data;

// See IMetronService for the full contract and why this exists. Unlike
// GCD, Metron has no anonymous tier at all - every call needs the Basic
// Auth credentials from Settings (a free metron.cloud account), checked
// up front so a store that hasn't set one up just gets a clean "not
// configured" skip instead of a wasted network call every single issue.
public class MetronService : IMetronService
{
    private const string BaseUrl = "https://metron.cloud/api";
    private static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(400);
    private static readonly HttpClient Http = BuildClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IStoreSettingsService _storeSettings;

    public MetronService(IStoreSettingsService storeSettings)
    {
        _storeSettings = storeSettings;
    }

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComicPos/1.0 (personal single-store inventory tool)");
        // Same reasoning as GcdService's client: a Django REST Framework
        // API serves its HTML "browsable API" page instead of JSON unless
        // the request explicitly asks for JSON.
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public MetronCoverLookupResult FindAndDownloadCover(string seriesName, float issueNumber, int? startYear, string fileNameHint)
    {
        var settings = _storeSettings.Load();
        if (string.IsNullOrWhiteSpace(settings.MetronUsername) || string.IsNullOrWhiteSpace(settings.MetronPassword))
            return Fail("no Metron account configured in Settings", null);

        var numberText = issueNumber == Math.Floor(issueNumber)
            ? ((int)issueNumber).ToString(CultureInfo.InvariantCulture)
            : issueNumber.ToString(CultureInfo.InvariantCulture);

        var query = $"series_name={Uri.EscapeDataString(seriesName)}&number={Uri.EscapeDataString(numberText)}";
        if (startYear != null) query += $"&series_year_began={startYear}";
        var url = $"{BaseUrl}/issue/?{query}";

        string json;
        try
        {
            Thread.Sleep(RequestDelay);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.MetronUsername}:{settings.MetronPassword}")));

            using var response = Http.Send(request);
            json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return RateLimited($"429 from {url}");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return Blocked($"403 from {url}");
            if (!response.IsSuccessStatusCode)
                return Fail($"Metron request failed: HTTP {(int)response.StatusCode}", $"{response.ReasonPhrase} ({url})");

            // metron.cloud's own website returned an HTML "Access Denied"
            // bot-protection page (Anubis) during testing, not a 403 - a
            // 200 OK with HTML instead of JSON. Caught explicitly here so
            // that failure mode is unmistakable in the trace log instead
            // of showing up as a confusing JSON-parse error.
            if (json.TrimStart().StartsWith("<"))
                return Blocked($"got an HTML page instead of JSON (likely bot-protection) - response started with \"{Truncate(json, 80)}\" ({url})");
        }
        catch (Exception ex)
        {
            return Fail($"Metron request failed: {ex.GetType().Name}", $"{ex.Message} ({url})");
        }

        MetronIssueListResponse? list;
        try
        {
            list = JsonSerializer.Deserialize<MetronIssueListResponse>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return Fail("Metron returned unparseable data", $"{ex.Message} - response started with \"{Truncate(json, 80)}\"");
        }

        var matches = list?.results;
        if (matches == null || matches.Count == 0)
            return Fail("no match in Metron for this series/issue/year", $"{seriesName} #{numberText}" + (startYear != null ? $" ({startYear})" : " (no start year given)"));

        // Prefer whichever match actually has a cover image on file - a
        // series/number match with no image is no better than no match
        // at all for our purposes.
        var chosen = matches.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.image)) ?? matches[0];
        if (string.IsNullOrWhiteSpace(chosen.image))
            return Fail("Metron has this issue on file but no cover image set for it", null);

        byte[] bytes;
        try
        {
            Thread.Sleep(RequestDelay);
            using var imgRequest = new HttpRequestMessage(HttpMethod.Get, chosen.image);
            using var imgResponse = Http.Send(imgRequest);

            if (imgResponse.StatusCode == HttpStatusCode.TooManyRequests)
                return RateLimited($"429 downloading cover ({chosen.image})");
            if (imgResponse.StatusCode == HttpStatusCode.Forbidden)
                return Blocked($"403 downloading cover ({chosen.image})");
            imgResponse.EnsureSuccessStatusCode();

            bytes = imgResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Fail($"Metron cover image download failed: {ex.GetType().Name}", $"{ex.Message} ({chosen.image})");
        }

        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos", "CoverImages");
            Directory.CreateDirectory(folder);

            var ext = Path.GetExtension(new Uri(chosen.image).LocalPath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

            var invalid = Path.GetInvalidFileNameChars();
            var safeName = new string(fileNameHint.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            var path = Path.Combine(folder, $"{safeName}{ext}");

            File.WriteAllBytes(path, bytes);
            return new MetronCoverLookupResult { ImagePath = path, Reason = "found via Metron" };
        }
        catch (Exception ex)
        {
            return Fail($"couldn't save the downloaded cover: {ex.GetType().Name}", ex.Message);
        }
    }

    private static MetronCoverLookupResult Fail(string reason, string? detail) =>
        new MetronCoverLookupResult { ImagePath = null, Reason = reason, Detail = detail };

    private static MetronCoverLookupResult RateLimited(string detail) =>
        new MetronCoverLookupResult { ImagePath = null, Reason = "Metron rate limit hit (429) - stop using it for the rest of this run", Detail = detail, IsRateLimited = true };

    private static MetronCoverLookupResult Blocked(string detail) =>
        new MetronCoverLookupResult { ImagePath = null, Reason = "Metron blocked this app (403/bot-protection) - stop using it for the rest of this run", Detail = detail, IsRateLimited = true };

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    private class MetronIssueListResponse
    {
        public int count { get; set; }
        public string? next { get; set; }
        public List<MetronIssueDto>? results { get; set; }
    }

    // Matches Metron's CommonIssue shape (the list-result row) - confirmed
    // via the Metron Python wrapper's own published schema docs to include
    // "image" directly, unlike GCD where the list view needs a second
    // per-issue detail call to get the cover URL.
    private class MetronIssueDto
    {
        public int id { get; set; }
        public string? number { get; set; }
        public string? image { get; set; }
    }
}
