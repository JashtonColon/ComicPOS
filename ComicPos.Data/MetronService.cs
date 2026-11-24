using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.Data;

// See IMetronService for the full contract and why this exists. Unlike
// GCD, Metron has no anonymous tier at all - every call needs the API
// token from Settings (generated from a free metron.cloud account),
// checked up front so a store that hasn't set one up just gets a clean
// "not configured" skip instead of a wasted network call every single
// issue. Metron used to accept plain username/password (HTTP Basic
// Auth) but dropped that in favor of a Bearer API token.
public class MetronService : IMetronService
{
    private const string BaseUrl = "https://metron.cloud/api";
    private static readonly HttpClient Http = BuildClient();
    // AllowReadingFromString is required, not optional - confirmed live
    // (2026-11-23, EnrichIssueDetails' first real run): Metron's own API
    // (a Django REST Framework DecimalField) serializes "price" as a
    // JSON STRING ("3.99"), not a bare number, which System.Text.Json
    // refuses to deserialize into decimal/decimal? by default ("The JSON
    // value could not be converted to System.Nullable`1[System.Decimal]").
    // This silently broke every single EnrichIssueDetails call across an
    // 809-issue run - every one logged a detail-enrichment error and fell
    // back to Price=0/a generated barcode instead of Metron's real data.
    // FindIssueByUpc reads the same detail.price field and would have hit
    // the exact same failure the first time anything actually called it
    // live (flagged as untested in IMetronService's own doc comment).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    // Metron's documented limit is 20 requests/minute per account. The
    // first real run against it (2026-11-21) hit a 429 after only ~21
    // issues (roughly 40 requests - a list call plus an image download
    // each) because requests were only 400ms apart with no shared pacing
    // across calls. This enforces a minimum gap between EVERY request
    // this service makes (list or image, any issue) so a long Re-scan run
    // stays under the limit instead of racing to a wall and stopping for
    // the rest of the run after a couple dozen issues. Static/shared
    // because MetronService is registered as a singleton - one pacing
    // clock for the whole app, which is what a per-account limit needs.
    private static readonly object RateLimitLock = new();
    private static DateTime _lastRequestAtUtc = DateTime.MinValue;
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(3500);

    private static void WaitForRateLimit()
    {
        lock (RateLimitLock)
        {
            var wait = MinRequestInterval - (DateTime.UtcNow - _lastRequestAtUtc);
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            _lastRequestAtUtc = DateTime.UtcNow;
        }
    }

    private readonly IStoreSettingsService _storeSettings;

    public MetronService(IStoreSettingsService storeSettings)
    {
        _storeSettings = storeSettings;
    }

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        // Was 20s - raised after a live run (2026-11-23) showed every
        // single /api/issue/?series_name=...&number=... search (the
        // query FindIssueId/FindAndDownloadCover use) hitting exactly
        // this timeout via TaskCanceledException, while other Metron
        // calls (the bulk date-range listing Check Now uses) complete
        // fine - that specific search endpoint just appears to be
        // genuinely slower than 20s on Metron's end, not rate-limited or
        // blocked (no 429/403 - the request never got a response at
        // all). 60s is a guess at enough headroom, not a measured value -
        // may need to go higher if it's still not enough.
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ComicPos/1.0 (personal single-store inventory tool)");
        // Same reasoning as GcdService's client: a Django REST Framework
        // API serves its HTML "browsable API" page instead of JSON unless
        // the request explicitly asks for JSON.
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public MetronCoverLookupResult FindAndDownloadCover(string seriesName, float issueNumber, DateOnly? expectedDate, string fileNameHint, int maxCoversNeeded = 1)
    {
        var settings = _storeSettings.Load();
        if (string.IsNullOrWhiteSpace(settings.MetronApiToken))
            return Fail("no Metron API token configured in Settings", null);

        var numberText = issueNumber == Math.Floor(issueNumber)
            ? ((int)issueNumber).ToString(CultureInfo.InvariantCulture)
            : issueNumber.ToString(CultureInfo.InvariantCulture);

        // No series_year_began filter here on purpose (there used to be
        // one) - that parameter means the YEAR THE VOLUME BEGAN, not this
        // issue's own release date, and we have no reliable value for
        // the former (confirmed live: neither our stored series.StartYear
        // nor this issue's own release year satisfied it for a real,
        // currently-publishing Wonder Woman issue - every request came
        // back with zero results). Metron's series_name + number search
        // typically returns a handful of candidates (one per same-named
        // volume across history, at most) - cheap enough to fetch them
        // all and pick the right one client-side below, by comparing
        // each candidate's own cover_date against expectedDate instead.
        var query = $"series_name={Uri.EscapeDataString(seriesName)}&number={Uri.EscapeDataString(numberText)}";
        var url = $"{BaseUrl}/issue/?{query}";

        string json;
        try
        {
            WaitForRateLimit();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.MetronApiToken);

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
            return Fail("no match in Metron for this series/issue/year", $"{seriesName} #{numberText}" + (expectedDate != null ? $" ({expectedDate:yyyy-MM-dd})" : " (no date given)"));

        var withImage = matches.Where(m => !string.IsNullOrWhiteSpace(m.image)).ToList();
        if (withImage.Count == 0)
            return Fail("Metron has this issue on file but no cover image set for it", null);

        // More than one same-named volume can share this exact issue
        // number (a character relaunched across decades, like Wonder
        // Woman, publishes a "#39" more than once) - whichever result's
        // own cover_date sits closest to expectedDate is almost always
        // the right one, since that date is specific to one real-world
        // printing. Falls back to the first result with an image when
        // expectedDate or cover_date isn't available to compare at all
        // (the old behavior) rather than guessing at a date difference
        // that doesn't mean anything.
        var chosen = expectedDate != null
            ? withImage
                .OrderBy(m => m.cover_date.HasValue
                    ? Math.Abs(m.cover_date.Value.DayNumber - expectedDate.Value.DayNumber)
                    : int.MaxValue)
                .First()
            : withImage[0];

        // Only the primary cover by default - exactly the old behavior,
        // no extra API call. More than one cover actually needed (see
        // maxCoversNeeded's own doc comment on IMetronService) pulls in
        // the matched issue's own detail endpoint for its real variant
        // list instead of reusing this one image for every Cover row.
        var candidates = new List<(string? Label, string Url)> { (null, chosen.image!) };

        if (maxCoversNeeded > 1)
        {
            try
            {
                WaitForRateLimit();
                var detailUrl = $"{BaseUrl}/issue/{chosen.id}/";
                using var detailRequest = new HttpRequestMessage(HttpMethod.Get, detailUrl);
                detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.MetronApiToken);
                using var detailResponse = Http.Send(detailRequest);
                if (detailResponse.IsSuccessStatusCode)
                {
                    var detailJson = detailResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!detailJson.TrimStart().StartsWith("<"))
                    {
                        var detail = JsonSerializer.Deserialize<MetronIssueDetailDto>(detailJson, JsonOptions);
                        if (detail?.variants != null)
                        {
                            foreach (var v in detail.variants)
                            {
                                if (!string.IsNullOrWhiteSpace(v.image))
                                    candidates.Add((v.name, v.image!));
                            }
                        }
                    }
                }
                // A failed/unparseable detail call just means "no variants
                // found" here - the primary cover already in candidates
                // still goes out below rather than losing the whole
                // lookup over this bonus call.
            }
            catch
            {
                // Same reasoning - primary cover alone is still a real result.
            }
        }

        var allCovers = new List<CoverCandidate>();
        var i = 0;
        foreach (var (label, candidateUrl) in candidates.Take(Math.Max(1, maxCoversNeeded)))
        {
            var hint = i == 0 ? fileNameHint : $"{fileNameHint}_{i}";
            i++;
            var (path, reason, detail, rateLimited) = DownloadImage(candidateUrl, hint);
            if (rateLimited)
            {
                if (allCovers.Count == 0) return RateLimited(detail ?? reason ?? "429/403 downloading cover");
                break; // keep whatever already downloaded - don't ask Metron for more right now
            }
            if (path != null) allCovers.Add(new CoverCandidate { Label = label, ImagePath = path });
        }

        if (allCovers.Count == 0)
            return Fail("Metron cover image download failed", "every candidate image for this issue failed to download");

        return new MetronCoverLookupResult { ImagePath = allCovers[0].ImagePath, Reason = "found via Metron", AllCovers = allCovers };
    }

    // Same series_name+number search/pick logic as FindAndDownloadCover
    // above, just returning the matched id instead of downloading
    // anything - see IMetronService.FindIssueId for why this exists
    // (recovering a Metron issue id for an issue added before
    // Issue.MetronIssueId was persisted at all).
    //
    // Retries specifically on 429 with a real backoff (20s, then 40s)
    // before giving up, and always returns a FailureReason alongside a
    // null Id - added after a live run (2026-11-23) came back 0/806 with
    // every single issue reporting the exact same generic "couldn't
    // find it" failure. That's indistinguishable from Metron genuinely
    // not having the issue UNLESS this records which it actually was,
    // and a sustained 429 storm is exactly what this service's normal
    // 3.5s-apart pacing (WaitForRateLimit) can't protect against if
    // something else is sharing the same Metron account/token at the
    // same time (e.g. another copy of this app left running elsewhere).
    public (int? Id, string? FailureReason) FindIssueId(string seriesName, float issueNumber, DateOnly? expectedDate)
    {
        var settings = _storeSettings.Load();
        if (string.IsNullOrWhiteSpace(settings.MetronApiToken))
            return (null, "no Metron API token configured in Settings");

        var numberText = issueNumber == Math.Floor(issueNumber)
            ? ((int)issueNumber).ToString(CultureInfo.InvariantCulture)
            : issueNumber.ToString(CultureInfo.InvariantCulture);
        var query = $"series_name={Uri.EscapeDataString(seriesName)}&number={Uri.EscapeDataString(numberText)}";
        var url = $"{BaseUrl}/issue/?{query}";

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string json;
            try
            {
                WaitForRateLimit();
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.MetronApiToken);
                using var response = Http.Send(request);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(20 * attempt));
                        continue;
                    }
                    return (null, $"429 (rate limited) from {url} after {maxAttempts} attempts");
                }
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    return (null, $"403 (blocked) from {url}");
                if (!response.IsSuccessStatusCode)
                    return (null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {url}");

                json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (json.TrimStart().StartsWith("<"))
                    return (null, $"got an HTML page instead of JSON (likely bot-protection) from {url}");
            }
            catch (Exception ex)
            {
                return (null, $"{ex.GetType().Name}: {ex.Message} ({url})");
            }

            MetronIssueListResponse? list;
            try
            {
                list = JsonSerializer.Deserialize<MetronIssueListResponse>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                return (null, $"unparseable response: {ex.Message}");
            }

            var matches = list?.results;
            if (matches == null || matches.Count == 0)
                return (null, $"no match in Metron for series_name={seriesName}&number={numberText}");

            var chosen = expectedDate != null
                ? matches
                    .OrderBy(m => m.cover_date.HasValue
                        ? Math.Abs(m.cover_date.Value.DayNumber - expectedDate.Value.DayNumber)
                        : int.MaxValue)
                    .First()
                : matches[0];

            return (chosen.id, null);
        }

        // Unreachable - every path above either returns or continues the
        // retry loop, and the loop's last allowed iteration always
        // returns instead of continuing. Here only so the compiler can
        // see every path returns a value.
        return (null, "retry loop exhausted unexpectedly");
    }

    // Shared by FindAndDownloadCover's per-candidate loop and
    // FindIssueByUpc - downloads one image URL and saves it to the same
    // CoverImages folder every cover image in this app lands in. Never
    // throws; the tuple's Reason/Detail/RateLimited mirror Fail/
    // RateLimited/Blocked's own fields so a caller can wrap them into
    // whichever result type it needs.
    private static (string? Path, string? Reason, string? Detail, bool RateLimited) DownloadImage(string imageUrl, string fileNameHint)
    {
        byte[] bytes;
        try
        {
            WaitForRateLimit();
            using var imgRequest = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            using var imgResponse = Http.Send(imgRequest);

            if (imgResponse.StatusCode == HttpStatusCode.TooManyRequests)
                return (null, "Metron rate limit hit (429) - stop using it for the rest of this run", $"429 downloading cover ({imageUrl})", true);
            if (imgResponse.StatusCode == HttpStatusCode.Forbidden)
                return (null, "Metron blocked this app (403/bot-protection) - stop using it for the rest of this run", $"403 downloading cover ({imageUrl})", true);
            imgResponse.EnsureSuccessStatusCode();

            bytes = imgResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return (null, $"Metron cover image download failed: {ex.GetType().Name}", $"{ex.Message} ({imageUrl})", false);
        }

        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos", "CoverImages");
            Directory.CreateDirectory(folder);

            var ext = Path.GetExtension(new Uri(imageUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

            var invalid = Path.GetInvalidFileNameChars();
            var safeName = new string(fileNameHint.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            var path = Path.Combine(folder, $"{safeName}{ext}");

            File.WriteAllBytes(path, bytes);
            return (path, null, null, false);
        }
        catch (Exception ex)
        {
            return (null, $"couldn't save the downloaded cover: {ex.GetType().Name}", ex.Message, false);
        }
    }

    // Looks an issue up directly by UPC instead of by series name +
    // number - see IMetronService.FindIssueByUpc for the full contract
    // and the (unconfirmed-live) assumption this rests on.
    public MetronUpcLookupResult FindIssueByUpc(string upc)
    {
        var settings = _storeSettings.Load();
        if (string.IsNullOrWhiteSpace(settings.MetronApiToken))
            return UpcFail("no Metron API token configured in Settings", null);

        var trimmed = upc.Trim();
        if (trimmed.Length == 0)
            return UpcFail("no barcode given", null);

        var url = $"{BaseUrl}/issue/?upc={Uri.EscapeDataString(trimmed)}";
        string json;
        try
        {
            WaitForRateLimit();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.MetronApiToken);
            using var response = Http.Send(request);
            json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return UpcRateLimited($"429 from {url}");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return UpcBlocked($"403 from {url}");
            if (!response.IsSuccessStatusCode)
                return UpcFail($"Metron request failed: HTTP {(int)response.StatusCode}", $"{response.ReasonPhrase} ({url})");
            if (json.TrimStart().StartsWith("<"))
                return UpcBlocked($"got an HTML page instead of JSON (likely bot-protection) - response started with \"{Truncate(json, 80)}\" ({url})");
        }
        catch (Exception ex)
        {
            return UpcFail($"Metron request failed: {ex.GetType().Name}", $"{ex.Message} ({url})");
        }

        MetronIssueListResponse? list;
        try
        {
            list = JsonSerializer.Deserialize<MetronIssueListResponse>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return UpcFail("Metron returned unparseable data", $"{ex.Message} - response started with \"{Truncate(json, 80)}\"");
        }

        var matches = list?.results;
        if (matches == null || matches.Count == 0)
            return UpcFail("no match in Metron for this barcode", trimmed);

        var matchedId = matches[0].id;
        var detailUrl = $"{BaseUrl}/issue/{matchedId}/";
        MetronIssueDetailDto? detail;
        try
        {
            WaitForRateLimit();
            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, detailUrl);
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.MetronApiToken);
            using var detailResponse = Http.Send(detailRequest);

            if (detailResponse.StatusCode == HttpStatusCode.TooManyRequests)
                return UpcRateLimited($"429 from {detailUrl}");
            if (detailResponse.StatusCode == HttpStatusCode.Forbidden)
                return UpcBlocked($"403 from {detailUrl}");

            var detailJson = detailResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (detailJson.TrimStart().StartsWith("<"))
                return UpcBlocked($"got an HTML page instead of JSON (likely bot-protection) ({detailUrl})");

            detail = JsonSerializer.Deserialize<MetronIssueDetailDto>(detailJson, JsonOptions);
        }
        catch (Exception ex)
        {
            return UpcFail($"Metron detail request failed: {ex.GetType().Name}", $"{ex.Message} ({detailUrl})");
        }

        if (detail?.series == null || string.IsNullOrWhiteSpace(detail.series.name))
            return UpcFail("Metron matched the barcode but gave no usable issue detail", detailUrl);

        // Which image/cover-letter this scanned UPC actually belongs to -
        // the primary issue (Cover A, effectively) or one specific
        // variant, each of which can carry its own distinct upc.
        string? chosenImage = detail.image;
        var coverLetterGuess = "A";
        if (!string.Equals(detail.upc?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase) && detail.variants != null)
        {
            var variantMatch = detail.variants.FirstOrDefault(v => string.Equals(v.upc?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            if (variantMatch != null)
            {
                chosenImage = variantMatch.image;
                coverLetterGuess = GuessCoverLetterFromVariantName(variantMatch.name) ?? "A";
            }
        }

        string? imagePath = null;
        if (!string.IsNullOrWhiteSpace(chosenImage))
        {
            var hint = $"upc_{trimmed}_{Guid.NewGuid():N}".Replace(" ", "_");
            var (path, _, _, _) = DownloadImage(chosenImage!, hint);
            imagePath = path;
        }

        string Role(string roleName) =>
            detail.credits?.FirstOrDefault(c => c.role != null && c.role.Any(r => string.Equals(r.name, roleName, StringComparison.OrdinalIgnoreCase)))?.creator ?? "";

        float.TryParse(detail.number, NumberStyles.Float, CultureInfo.InvariantCulture, out var issueNumber);

        return new MetronUpcLookupResult
        {
            Reason = "found via Metron UPC lookup",
            SeriesName = detail.series.name,
            SeriesStartYear = detail.series.year_began is > 0 ? detail.series.year_began : null,
            IssueNumber = issueNumber,
            Publisher = detail.publisher?.name,
            Price = detail.price,
            StoreDate = detail.store_date,
            FocDate = detail.foc_date,
            CoverLetterGuess = coverLetterGuess,
            DownloadedImagePath = imagePath,
            Author = Role("Writer"),
            Penciler = Role("Penciler"),
            Inker = Role("Inker"),
            Colorist = Role("Colorist"),
            Letterer = Role("Letterer"),
        };
    }

    // Parses a cover letter out of Metron's own free-text variant name
    // ("Cover B", "2nd Printing Cover C Variant" -> "C") - best-effort
    // only, since nothing guarantees a variant name follows this pattern
    // at all (e.g. "Virgin Variant" has no letter in it anywhere).
    // Now a thin wrapper over the shared parser (ComicPos.Core.
    // CoverLetterParser) - used to have its own private copy of this
    // exact regex, now also used by LocgService and AutoFetchService
    // (see CoverLetterParser's own doc comment for why unanchored).
    private static string? GuessCoverLetterFromVariantName(string? name)
        => ComicPos.Core.CoverLetterParser.GuessFromName(name);

    // Lists every issue Metron has on file with a store/street date in
    // [after, before] - see IMetronService for why this exists (the
    // primary "Check Now" discovery source as of 2026-11-22, replacing
    // LOCG) and GetUpToDate the pagination/pacing notes. One list call
    // per page (store_date_range_after/_before - confirmed supported via
    // Metron's own API-best-practices docs), following "next" until
    // Metron says there isn't one, each page paced the same as every
    // other Metron request (WaitForRateLimit). Deliberately does NOT
    // fetch each issue's own detail endpoint here - Publisher/Price/Upc
    // live only on that per-issue detail response, and paying for one
    // extra call per result (hundreds, industry-wide, for any real date
    // range) would turn one cheap list call into the exact kind of
    // per-issue cost this method exists to avoid. Those three fields are
    // left blank here and filled in later, ONLY for genuinely new
    // issues, by EnrichIssueDetails below (see AutoFetchService.
    // BuildCoverFromEntry and its blank-Publisher passthrough in
    // AddEntriesAsIssues).
    public List<ComicListEntry> GetIssuesInDateRange(DateOnly after, DateOnly before, Action<string>? onProgress = null)
    {
        var entries = new List<ComicListEntry>();
        var settings = _storeSettings.Load();
        if (string.IsNullOrWhiteSpace(settings.MetronApiToken))
        {
            onProgress?.Invoke("Metron: no API token configured in Settings - nothing fetched.");
            return entries;
        }

        var url = $"{BaseUrl}/issue/?store_date_range_after={after:yyyy-MM-dd}&store_date_range_before={before:yyyy-MM-dd}";
        var page = 1;

        while (!string.IsNullOrWhiteSpace(url))
        {
            string json;
            try
            {
                WaitForRateLimit();
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.MetronApiToken);
                using var response = Http.Send(request);
                json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    onProgress?.Invoke($"Metron: rate limited (429) on page {page} - stopping here with {entries.Count} issue(s) found so far.");
                    break;
                }
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    onProgress?.Invoke($"Metron: blocked (403) on page {page} - stopping here with {entries.Count} issue(s) found so far.");
                    break;
                }
                if (!response.IsSuccessStatusCode)
                {
                    onProgress?.Invoke($"Metron: request failed (HTTP {(int)response.StatusCode}) on page {page} - stopping here with {entries.Count} issue(s) found so far.");
                    break;
                }
                if (json.TrimStart().StartsWith("<"))
                {
                    onProgress?.Invoke($"Metron: got an HTML page instead of JSON on page {page} (likely bot-protection) - stopping here with {entries.Count} issue(s) found so far.");
                    break;
                }
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"Metron: request failed on page {page}: {ex.Message} - stopping here with {entries.Count} issue(s) found so far.");
                break;
            }

            MetronIssueListResponse? list;
            try
            {
                list = JsonSerializer.Deserialize<MetronIssueListResponse>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"Metron: page {page} returned unparseable data ({ex.Message}) - stopping here with {entries.Count} issue(s) found so far.");
                break;
            }

            foreach (var m in list?.results ?? new List<MetronIssueDto>())
            {
                // No series identity at all means nothing to match/create
                // a local Series row against - skip rather than guess.
                if (m.series == null || string.IsNullOrWhiteSpace(m.series.name)) continue;

                float.TryParse(m.number, NumberStyles.Float, CultureInfo.InvariantCulture, out var issueNumber);

                entries.Add(new ComicListEntry
                {
                    RawTitle = $"{m.series.name} #{m.number}",
                    SeriesName = m.series.name,
                    SeriesStartYearHint = m.series.year_began is > 0 ? m.series.year_began : null,
                    MetronSeriesId = m.series.id,
                    MetronIssueId = m.id,
                    IssueNumber = issueNumber,
                    CoverLetter = "A",
                    // Left blank on purpose - see this method's own doc
                    // comment above. AddEntriesAsIssues treats a blank
                    // Publisher as "unknown, don't filter it out" rather
                    // than "not tracked."
                    Publisher = "",
                    ReleaseDate = m.store_date,
                    FocDate = m.foc_date,
                    CoverImageUrl = m.image
                });
            }

            onProgress?.Invoke($"Metron: page {page} - {list?.results?.Count ?? 0} issue(s) ({entries.Count} total so far).");
            url = list?.next;
            page++;
        }

        return entries;
    }

    // Fills in the handful of fields Metron's list call doesn't carry
    // (Publisher/Price/Upc - see GetIssuesInDateRange) via one detail
    // call (GET /issue/{id}/) for a single issue Metron's own numeric id
    // identifies exactly - no name/number ambiguity possible, unlike
    // FindAndDownloadCover's search. Mirrors ILocgService.
    // EnrichIssueDetails' contract exactly (same "best-effort per brand-
    // new issue only, throws on total failure so the caller can log it,
    // mutates entry in place" shape) so AutoFetchService.
    // BuildCoverFromEntry can treat both sources the same way.
    public void EnrichIssueDetails(ComicListEntry entry)
    {
        if (!entry.MetronIssueId.HasValue)
            throw new InvalidOperationException("This entry has no Metron issue id to enrich from.");

        var settings = _storeSettings.Load();
        if (string.IsNullOrWhiteSpace(settings.MetronApiToken))
            throw new InvalidOperationException("No Metron API token configured in Settings.");

        var detailUrl = $"{BaseUrl}/issue/{entry.MetronIssueId.Value}/";
        WaitForRateLimit();
        using var request = new HttpRequestMessage(HttpMethod.Get, detailUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.MetronApiToken);
        using var response = Http.Send(request);
        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Metron detail request failed: HTTP {(int)response.StatusCode} ({detailUrl})");
        if (json.TrimStart().StartsWith("<"))
            throw new InvalidOperationException($"Metron returned an HTML page instead of JSON (likely bot-protection) for {detailUrl}");

        MetronIssueDetailDto? detail;
        try
        {
            detail = JsonSerializer.Deserialize<MetronIssueDetailDto>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Metron detail response was unparseable: {ex.Message} ({detailUrl})");
        }

        if (detail == null)
            throw new InvalidOperationException($"Metron detail response was empty ({detailUrl})");

        if (!string.IsNullOrWhiteSpace(detail.upc)) entry.Upc = detail.upc;
        if (detail.price.HasValue) entry.Price = detail.price;
        if (!string.IsNullOrWhiteSpace(detail.publisher?.name)) entry.Publisher = detail.publisher!.name!;
        // store_date/foc_date/image already came from the cheaper list
        // call in GetIssuesInDateRange - only overwritten here if the
        // detail endpoint actually has something, never blanked out by
        // a missing detail field.
        if (detail.foc_date.HasValue) entry.FocDate = detail.foc_date;
        if (detail.store_date.HasValue) entry.ReleaseDate = detail.store_date;
        if (!string.IsNullOrWhiteSpace(detail.image)) entry.CoverImageUrl = detail.image;
        if (!string.IsNullOrWhiteSpace(detail.desc)) entry.Description = detail.desc;

        // Every other cover Metron has on file for this exact issue -
        // the list call (GetIssuesInDateRange) never returns these, only
        // this detail call does. AutoFetchService.BuildCoversFromEntry
        // turns each one into its own Cover row (own image/UPC/price),
        // right after this call returns. Skips a variant with no image
        // at all - nothing to show, and no point creating a blank Cover
        // row for it.
        entry.Variants = (detail.variants ?? new List<MetronVariantDto>())
            .Where(v => !string.IsNullOrWhiteSpace(v.image))
            .Select(v => new MetronVariantInfo
            {
                Name = v.name,
                Upc = v.upc,
                Price = v.price,
                ImageUrl = v.image
            })
            .ToList();
    }

    private static MetronUpcLookupResult UpcFail(string reason, string? detail) =>
        new MetronUpcLookupResult { Reason = reason, Detail = detail };

    private static MetronUpcLookupResult UpcRateLimited(string detail) =>
        new MetronUpcLookupResult { Reason = "Metron rate limit hit (429) - stop using it for the rest of this run", Detail = detail, IsRateLimited = true };

    private static MetronUpcLookupResult UpcBlocked(string detail) =>
        new MetronUpcLookupResult { Reason = "Metron blocked this app (403/bot-protection) - stop using it for the rest of this run", Detail = detail, IsRateLimited = true };

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

        // Added for client-side date matching (see FindAndDownloadCover) -
        // confirmed field name via the mokkari Python wrapper's published
        // CommonIssue schema, which documents this as a plain ISO date
        // ("yyyy-MM-dd"); System.Text.Json deserializes that straight
        // into a DateOnly natively on .NET 7+, no custom converter needed.
        public DateOnly? cover_date { get; set; }

        // The rest of these are only populated when this DTO is used for
        // the LIST shape mokkari calls BaseIssue (store_date/foc_date are
        // actually on CommonIssue itself and so were always present on
        // every /issue/ list call - just never read until
        // GetIssuesInDateRange needed them; series is BaseIssue's own
        // addition over CommonIssue). FindAndDownloadCover's existing
        // series_name+number search never reads any of these - adding
        // them here is purely additive.
        public DateOnly? store_date { get; set; }
        public DateOnly? foc_date { get; set; }
        public MetronIssueSeriesDto? series { get; set; }
    }

    // The per-issue DETAIL view (GET /issue/{id}/) - a much richer shape
    // than CommonIssue above, confirmed field-for-field via mokkari's
    // published Issue schema docs (mokkari.readthedocs.io/.../schemas/
    // issue/). Only used for two things so far: variants (multi-cover
    // backfill, see FindAndDownloadCover) and the whole-issue lookup in
    // FindIssueByUpc - fields neither of those need (arcs, characters,
    // reprints, etc.) are left out entirely rather than modeled for no
    // reason.
    private class MetronIssueDetailDto
    {
        public string? number { get; set; }
        public DateOnly? store_date { get; set; }
        public DateOnly? foc_date { get; set; }
        public string? image { get; set; }
        public MetronGenericItem? publisher { get; set; }
        public MetronIssueSeriesDto? series { get; set; }
        public decimal? price { get; set; }
        public string? upc { get; set; }
        public List<MetronCreditDto>? credits { get; set; }
        public List<MetronVariantDto>? variants { get; set; }
        // The solicitation/summary text - Metron calls this field "desc"
        // (confirmed via mokkari's own Issue schema). This is what the
        // New Books detail popup shows as an issue's description -
        // previously only comicade.com had an equivalent, so Metron-
        // sourced issues always showed a blank description.
        public string? desc { get; set; }
    }

    // Metron's generic {id, name} shape, reused for publisher/rating/role
    // entries - confirmed via mokkari's GenericItem schema docs.
    private class MetronGenericItem
    {
        public string? name { get; set; }
    }

    // Confirmed via mokkari's IssueSeries schema docs (nested inside the
    // Issue detail response, distinct from the flatter "series" object
    // mokkari documents elsewhere for other endpoints).
    private class MetronIssueSeriesDto
    {
        // Metron's own numeric id for this exact volume - present on the
        // LIST shape's nested series (mokkari's BasicSeries) and used by
        // GetIssuesInDateRange as Series.MetronSeriesId's source; this
        // class is also reused for the DETAIL response's own nested
        // series object (mokkari's IssueSeries), which may or may not
        // carry this same field - left null there rather than assumed,
        // since nothing in this app currently depends on it being set
        // from a detail call.
        public int? id { get; set; }
        public string? name { get; set; }

        // Nullable, unlike mokkari's own "int" - defensive against Metron
        // simply omitting this for a series it hasn't fully catalogued
        // yet, which would otherwise throw deserializing a JSON null into
        // a non-nullable int.
        public int? year_began { get; set; }
    }

    // Confirmed via mokkari's Credit schema docs: role is a LIST of
    // GenericItem (an issue can credit one person with more than one
    // role on the same entry), not a single string.
    private class MetronCreditDto
    {
        public string? creator { get; set; }
        public List<MetronGenericItem>? role { get; set; }
    }

    // Confirmed via mokkari's Variant schema docs - a variant cover's own
    // name/upc/image, independent of the primary issue's.
    private class MetronVariantDto
    {
        public string? name { get; set; }
        public string? upc { get; set; }
        public string? image { get; set; }
        // Confirmed on Metron's own Variant schema (mokkari) - each
        // variant can carry its own price, distinct from the primary
        // issue's price (a foil/virgin variant is almost always priced
        // higher). Was being silently discarded before - the only place
        // a variant's price was captured at all.
        public decimal? price { get; set; }
    }
}
