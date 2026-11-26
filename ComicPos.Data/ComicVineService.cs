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
        RequireOkStatus(doc, $"searching volumes for \"{seriesName}\"");

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

        // Used to be a single limit=100 call with no offset - fine for
        // CheckOneSeries' normal ~6-month rolling window (a volume rarely
        // ships more than a handful of issues in that span), but
        // RescanExistingBooks (RescanSeriesCovers) deliberately widens this
        // range to a series' ENTIRE known history to backfill old covers,
        // and a long-running ongoing title can easily have more than 100
        // issues/annuals/specials inside that window (confirmed live:
        // Nightwing, Swamp Thing and Catwoman all got re-linked to volumes
        // with 95-155+ issues on file in the same run this was found in).
        // Comic Vine returns only the first page with no error when a
        // filtered query matches more than `limit` rows - and sorted
        // store_date:asc, the page that gets dropped is always the NEWEST
        // issues, which are exactly the ones still missing a cover. This
        // pages through up to 1000 results instead of stopping at the
        // first 100, the same pattern GetNewNumberOneIssues already uses.
        var results = new List<ComicVineNewIssue>();
        for (var offset = 0; offset < 1000; offset += 100)
        {
            var url = "https://comicvine.gamespot.com/api/issues/" +
                       $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                       $"&filter={Uri.EscapeDataString(filter)}" +
                       "&field_list=issue_number,name,store_date,image" +
                       $"&sort=store_date:asc&limit=100&offset={offset}";

            var json = FetchString(url);
            using var doc = JsonDocument.Parse(json);
            RequireOkStatus(doc, $"fetching issues for volume {volumeId}");

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

                if (item.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
                    issue.ImageUrl = GetBestImageUrl(img);

                results.Add(issue);
            }

            if (pageCount < 100) break; // last page
        }

        return results;
    }

    // Added 2026-11-24 for the cover-image-pixelation fix (see
    // AutoFetchService.UpgradeLowResolutionCovers) - a narrow, one-issue
    // lookup for retroactively upgrading a cover that was already saved at
    // the old "medium_url" size, before GetBestImageUrl above existed.
    // Filters Comic Vine's own issue list to this exact volume + exact
    // issue number (the same filter shape GetIssuesInRange uses, just
    // narrowed with an extra issue_number clause) rather than fetching the
    // whole volume's issue list and matching client-side - cheaper, and it
    // means Comic Vine itself is doing the exact-match work, not this code.
    // Returns null (never throws) when nothing matches, when more than one
    // issue matches (shouldn't happen for a real volume+number pair, but a
    // bad/duplicate catalog entry is a reason to do nothing, not guess), or
    // when the one match has no image at all - every case means "leave
    // whatever cover is already on file alone".
    public string? FindBestCoverImageUrl(int volumeId, float issueNumber)
    {
        var apiKey = RequireApiKey();
        var numberText = issueNumber == Math.Floor(issueNumber)
            ? ((int)issueNumber).ToString()
            : issueNumber.ToString("0.#");
        var filter = $"volume:{volumeId},issue_number:{Uri.EscapeDataString(numberText)}";

        var url = "https://comicvine.gamespot.com/api/issues/" +
                   $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                   $"&filter={Uri.EscapeDataString(filter)}" +
                   "&field_list=issue_number,image&limit=5";

        var json = FetchString(url);
        using var doc = JsonDocument.Parse(json);
        RequireOkStatus(doc, $"looking up volume {volumeId} issue #{issueNumber:0.##} for a higher-resolution cover");

        if (!doc.RootElement.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? exactMatch = null;
        var matchCount = 0;
        foreach (var item in resultsEl.EnumerateArray())
        {
            if (!item.TryGetProperty("issue_number", out var num)) continue;
            var numStr = num.ValueKind == JsonValueKind.String ? num.GetString() : num.ToString();
            if (!float.TryParse(numStr, out var parsed) || parsed != issueNumber) continue;

            matchCount++;
            exactMatch = item;
        }

        if (matchCount != 1 || exactMatch == null) return null; // no match, or ambiguous - don't guess

        return exactMatch.Value.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object
            ? GetBestImageUrl(img)
            : null;
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
            RequireOkStatus(doc, "looking up new #1 issues");

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

                if (item.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
                    issue.ImageUrl = GetBestImageUrl(img);

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
        RequireOkStatus(doc, $"fetching details for volume {volumeId}");

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

    // A publisher's Comic Vine id never changes once assigned, and
    // finding it from scratch means paging through the ENTIRE publisher
    // catalog (see ResolvePublisherId) - thousands of entries, mostly
    // small/foreign press. Caching every id this process has ever
    // resolved, to a small file on disk, means that cost is paid at most
    // ONCE per publisher name, ever - not once per Check Now run, forever.
    // Keyed case-insensitively on the exact name as typed in Settings >
    // Tracked Publishers.
    private static Dictionary<string, int>? _publisherIdCache;
    private static readonly object PublisherCacheLock = new();

    private static string PublisherCacheFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos", "publisher-ids-cache.json");

    private static Dictionary<string, int> LoadPublisherIdCache()
    {
        lock (PublisherCacheLock)
        {
            if (_publisherIdCache != null) return _publisherIdCache;

            _publisherIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = PublisherCacheFilePath;
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path));
                    if (loaded != null)
                        foreach (var kv in loaded) _publisherIdCache[kv.Key] = kv.Value;
                }
            }
            catch
            {
                // A corrupt or unreadable cache file just means every
                // publisher gets re-resolved via the full scan below once
                // more - not a reason to crash the whole Check Now run.
            }
            return _publisherIdCache;
        }
    }

    private static void SavePublisherIdCache()
    {
        lock (PublisherCacheLock)
        {
            if (_publisherIdCache == null) return;
            try
            {
                var path = PublisherCacheFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(_publisherIdCache));
            }
            catch
            {
                // Best-effort - worst case, this same publisher pays the
                // full scan cost again next run instead of staying cached.
            }
        }
    }

    // Resolves a publisher name to its Comic Vine id. Shared by every
    // publisher-scoped lookup so the resolution logic (and its exact-name
    // match rule) lives in one place.
    // candidateNames carries back real publisher names Comic Vine's list
    // actually contains that loosely match the search text, matched or
    // not - so a caller whose lookup fails can tell the person what Comic
    // Vine really calls that publisher instead of guessing at an example
    // spelling that might itself be wrong.
    // diagnostic carries back how the scan ended when nothing matched, so
    // a failure can be debugged from the error message alone.
    //
    // This used to resolve publishers through Comic Vine's /search/
    // endpoint. Tested live against this store's own tracked publishers,
    // that turned out to be unusable for this: a search restricted to
    // "&resources=publisher" came back with a real-looking
    // number_of_total_results (tens of thousands - roughly the same
    // magnitude as an unrestricted search's total) but a literally EMPTY
    // results array, every single time, for Marvel, DC Comics, Image
    // Comics and every other tracked publisher - not just a misspelled or
    // obscure one. An unrestricted search came back with real rows, but
    // none of the top 10 (by relevance) were actually publisher-type
    // objects for any of these names - they were characters, volumes and
    // issues that happen to contain the same word. Whatever Comic Vine's
    // /search/ actually indexes a publisher's name under, it isn't
    // reachable this way for any publisher real stores would track.
    //
    // /publishers/ (the LIST endpoint, not search) returns genuine
    // publisher objects - no guessing about resource type - but doesn't
    // support filtering by name server-side (confirmed earlier:
    // "filter=name:X" silently returns zero rows for everything). So this
    // pages through the list itself and compares names client-side,
    // sorted by id ascending - Comic Vine assigns ids roughly in the
    // order a publisher was first added, and every major publisher
    // predates thousands of later small/foreign-press entries (Marvel is
    // id 31, DC Comics is id 10), so a real, long-established publisher
    // should turn up within the first page or two even though the full
    // catalog runs into the thousands. A hard cap stops a genuinely
    // nonexistent or very obscure name from scanning forever.
    private static int? ResolvePublisherId(string apiKey, string publisherName, out List<string> candidateNames, out string diagnostic)
    {
        candidateNames = new List<string>();
        var candidateIds = new List<int>();
        diagnostic = "";

        var cache = LoadPublisherIdCache();
        lock (PublisherCacheLock)
        {
            if (cache.TryGetValue(publisherName, out var cachedId))
                return cachedId;
        }

        const int pageSize = 100;
        const int hardCapRows = 3000; // ~30 calls - generous for even a recent small-press entry, bounded so a bad name fails predictably
        var offset = 0;
        int? foundId = null;

        while (offset < hardCapRows)
        {
            var url = "https://comicvine.gamespot.com/api/publishers/" +
                       $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                       $"&field_list=id,name&sort=id:asc&limit={pageSize}&offset={offset}";

            var json = FetchString(url);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("status_code", out var statusCode) &&
                statusCode.ValueKind == JsonValueKind.Number && statusCode.GetInt32() != 1)
            {
                var errorText = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                throw new InvalidOperationException(
                    $"Comic Vine's publisher list failed (status_code {statusCode.GetInt32()}" +
                    (string.IsNullOrWhiteSpace(errorText) ? ")" : $": {errorText})") +
                    $" while looking up \"{publisherName}\".");
            }

            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                diagnostic = $"no results array at offset {offset}";
                break;
            }

            var rowsThisPage = 0;
            foreach (var p in results.EnumerateArray())
            {
                rowsThisPage++;
                var name = GetString(p, "name");

                if (string.Equals(name, publisherName, StringComparison.OrdinalIgnoreCase) &&
                    p.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number)
                {
                    foundId = pid.GetInt32();
                    break;
                }

                // Checked both directions - a short real name like "Image"
                // wouldn't ever contain the longer search text "Image Comics"
                // (only the reverse would match), so only checking
                // name.Contains(publisherName) missed exactly that case
                // live: Comic Vine's real entry for the publisher most
                // stores call "Image Comics" is apparently just "Image".
                if (candidateNames.Count < 5 && !string.IsNullOrWhiteSpace(name) &&
                    (name.Contains(publisherName, StringComparison.OrdinalIgnoreCase) ||
                     publisherName.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    candidateNames.Add(name);
                    candidateIds.Add(p.TryGetProperty("id", out var cpid) && cpid.ValueKind == JsonValueKind.Number ? cpid.GetInt32() : -1);
                }
            }

            if (foundId != null) break;
            if (rowsThisPage < pageSize)
            {
                diagnostic = $"scanned the whole publisher list ({offset + rowsThisPage} entries) - no exact match";
                break;
            }

            offset += pageSize;
        }

        if (offset >= hardCapRows && foundId == null)
            diagnostic = $"gave up after scanning {hardCapRows} publishers without an exact match";

        // No exact (case-insensitive) match, but exactly one textually-
        // related publisher turned up across the whole scan - e.g. this
        // store's Tracked Publishers says "Image Comics" and Comic Vine's
        // actual entry is just "Image". One candidate out of thousands of
        // publishers scanned is confident enough to use automatically
        // instead of making the store go fix Settings > Tracked
        // Publishers by hand first and re-run. Two or more candidates
        // stays an error (ambiguous - a person should pick), and zero
        // candidates stays an error too (nothing to go on, like DSTLRY
        // when Comic Vine genuinely has no textually-similar entry).
        if (foundId == null && candidateNames.Count == 1 && candidateIds.Count == 1 && candidateIds[0] >= 0)
        {
            foundId = candidateIds[0];
            diagnostic = $"no exact match for \"{publisherName}\" - auto-matched to Comic Vine's \"{candidateNames[0]}\" (the only textually-similar publisher found)";
        }

        if (foundId != null)
        {
            lock (PublisherCacheLock)
            {
                cache[publisherName] = foundId.Value;
            }
            SavePublisherIdCache();
        }

        return foundId;
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

        // Used to silently return an empty list here when the publisher
        // name didn't resolve - which meant a typo'd or non-exact Tracked
        // Publishers entry produced a permanent, invisible "Comic Vine
        // discovery: +0 issue(s)" every single run, with no error and no
        // way to tell why. Throwing instead means DiscoverByTrackedPublishers's
        // existing per-publisher try/catch reports it in the Check Now
        // result, same as any other lookup failure - and it reports back
        // Comic Vine's OWN actual candidate names (see ResolvePublisherId)
        // rather than a guessed example that might itself be wrong.
        var publisherId = ResolvePublisherId(apiKey, publisherName, out var candidateNames, out var diagnostic);
        if (publisherId == null)
        {
            var hint = candidateNames.Count > 0
                ? $"Comic Vine's closest match(es) for \"{publisherName}\": {string.Join(", ", candidateNames.Select(c => $"\"{c}\""))} - use one of those exact spellings in Settings > Tracked Publishers."
                : $"Comic Vine's publisher search for \"{publisherName}\" returned no results at all - double-check the spelling in Settings > Tracked Publishers.";
            // Raw details of what both search attempts actually saw -
            // appended so the NEXT time this fires, the error message
            // itself says why instead of needing another guess-and-test
            // round trip.
            throw new InvalidOperationException($"Comic Vine has no publisher named exactly \"{publisherName}\". {hint} [diagnostic: {diagnostic}]");
        }

        var volumeUrl = "https://comicvine.gamespot.com/api/volumes/" +
                         $"?api_key={Uri.EscapeDataString(apiKey)}&format=json" +
                         $"&filter=publisher:{publisherId}" +
                         $"&sort=date_last_updated:desc&limit={limit}" +
                         "&field_list=id,name,start_year,publisher,count_of_issues";

        var volumeJson = FetchString(volumeUrl);
        using var volumeDoc = JsonDocument.Parse(volumeJson);
        RequireOkStatus(volumeDoc, $"listing volumes for publisher \"{publisherName}\"");

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
            var bytes = FetchBytes(imageUrl);

            // Some sources (confirmed: comicade.com) serve a generic "no
            // real art yet" stock graphic under what looks like a
            // completely normal, uniquely-numbered per-issue image URL -
            // nothing in the URL text gives it away. A hash match against
            // PlaceholderImageRegistry (built up by
            // AutoFetchService.ClearPlaceholderCovers finding the exact
            // same bytes repeated across many unrelated issues) means
            // this download isn't real cover art - returning null here
            // keeps the issue correctly counted as still missing a cover
            // instead of permanently locking in a placeholder that looks
            // "done" and never gets revisited once real art ships.
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (PlaceholderImageRegistry.IsKnownPlaceholder(hash))
                return null;

            var ext = Path.GetExtension(new Uri(imageUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

            return SaveBytesToCache(bytes, fileNameHint, ext);
        }
        catch
        {
            return null;
        }
    }

    // Same destination folder/naming convention DownloadCoverImage has
    // always used - distinct from DownloadCoverImage itself only in that
    // there's no network fetch and no placeholder-hash check (nothing to
    // be flaky about or to confuse with a source's own stock "no art yet"
    // graphic when the caller already has real bytes in hand), and a
    // genuine I/O failure is thrown rather than swallowed into a null -
    // see IComicVineService.SaveCoverImageBytes's own doc comment for why.
    public string SaveCoverImageBytes(byte[] imageBytes, string fileNameHint, string extension = ".jpg")
        => SaveBytesToCache(imageBytes, fileNameHint, extension);

    private static string SaveBytesToCache(byte[] bytes, string fileNameHint, string extension)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos", "CoverImages");
        Directory.CreateDirectory(folder);

        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(fileNameHint.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        var path = Path.Combine(folder, $"{safeName}{extension}");

        File.WriteAllBytes(path, bytes);
        return path;
    }

    // Comic Vine answers with HTTP 200 and a JSON body even when something
    // went wrong (a bad filter, an exhausted request quota, a stale API
    // key) - the real signal is this "status_code" field next to
    // "results", not the HTTP status line, which only ever reflects the
    // literal HTTP 420 FetchString/FetchBytes already retry on. Without
    // this check, every OTHER kind of Comic Vine error silently looked
    // like "zero results" to every caller but ResolvePublisherId (the one
    // place that already checked it) - which is exactly how a long
    // Re-scan Existing Books run once reported dozens of series as
    // "still missing covers, checked 0 issues from Comic Vine" when Comic
    // Vine was actually quietly refusing every one of those requests
    // after the store's 200-requests-per-resource-per-hour quota (see
    // comicvine.gamespot.com/api, Terms of Use) ran out mid-run.
    private static void RequireOkStatus(JsonDocument doc, string context)
    {
        if (!doc.RootElement.TryGetProperty("status_code", out var statusCode) ||
            statusCode.ValueKind != JsonValueKind.Number || statusCode.GetInt32() == 1)
            return; // 1 = OK, or no status_code field at all - nothing to flag

        var code = statusCode.GetInt32();
        var errorText = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;

        // Comic Vine doesn't document one fixed status_code for "you're
        // over quota" the way it documents HTTP 420 - and the hourly
        // quota above can run out well before a 420 ever shows up. So
        // instead of guessing a specific number, any error whose own text
        // mentions rate/limit/quota is treated the same as a real 420:
        // the message is built to contain "rate limit", which is exactly
        // what IsRateLimitError (AutoFetchService) checks for, so the
        // caller stops the whole batch instead of ploughing on and
        // mis-reporting every remaining series as "Comic Vine has no
        // cover art for these."
        if (!string.IsNullOrWhiteSpace(errorText) &&
            (errorText.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
             errorText.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
             errorText.Contains("quota", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Comic Vine rate limit hit (status_code {code}: {errorText}) while {context} - try again in a few minutes.");
        }

        throw new InvalidOperationException(
            $"Comic Vine returned an error (status_code {code}" +
            (string.IsNullOrWhiteSpace(errorText) ? ")" : $": {errorText})") +
            $" while {context}.");
    }

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // Added 2026-11-24 - both issue-fetching methods used to read only
    // "medium_url" (one of Comic Vine's smaller preview sizes, ~400px wide)
    // out of the "image" object every issue response already includes in
    // full regardless of field_list - root-causing persistent pixelation
    // whenever a Comic-Vine-sourced cover is shown any larger than that on
    // screen (e.g. the New Books detail modal's cover card, which is a
    // completely different element from the zoom-overlay's own, separately-
    // fixed, display-side scaling cap). "super_url" is comfortably larger
    // (a real display-quality size) without going all the way to
    // "original_url", which can be an enormous, unprocessed source scan.
    // Falls back to "medium_url" only if "super_url" genuinely isn't present
    // on this particular response (seen on some older/obscure entries) -
    // never fails the whole fetch over a missing image size.
    private static string? GetBestImageUrl(JsonElement img)
    {
        if (img.TryGetProperty("super_url", out var su) && su.ValueKind == JsonValueKind.String)
            return su.GetString();
        if (img.TryGetProperty("medium_url", out var mu) && mu.ValueKind == JsonValueKind.String)
            return mu.GetString();
        return null;
    }

    private string RequireApiKey()
    {
        var key = _storeSettings.Load().ComicVineApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Comic Vine API key is set. Go to Settings and add one (free from comicvine.gamespot.com/api/).");
        return key;
    }
}
