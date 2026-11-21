using System.Text.Json;

namespace ComicPos.Data;

// A small, shared, disk-persisted list of image-file hashes known to be a
// source's generic "no real art yet" stock graphic rather than real cover
// art for a specific issue. comicade.com (and possibly Comic Vine) will
// happily serve one of these under what looks exactly like a normal,
// uniquely-numbered per-issue image URL - confirmed live: a real
// solicited Amazing Spider-Man issue several months out showed a literal
// "Cover Coming Soon" graphic baked into the downloaded image itself,
// and its URL had nothing in the text (no "placeholder", "coming-soon",
// etc.) that gave it away. The only reliable tell is that the exact same
// image bytes turn up across many different, unrelated issues.
//
// See AutoFetchService.ClearPlaceholderCovers (discovers these
// empirically by hashing every cover image already on file and looking
// for a hash that repeats suspiciously often across different issues)
// and ComicVineService.DownloadCoverImage (checks every new download
// against this list before ever saving it as if it were real art, so a
// known placeholder never gets permanently locked in again once it's
// been identified once).
public static class PlaceholderImageRegistry
{
    private static HashSet<string>? _knownHashes;
    private static readonly object Lock = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos", "placeholder-image-hashes.json");

    public static bool IsKnownPlaceholder(string sha256Hex)
    {
        lock (Lock)
        {
            return Load().Contains(sha256Hex);
        }
    }

    public static void Add(string sha256Hex)
    {
        lock (Lock)
        {
            var set = Load();
            if (set.Add(sha256Hex))
                Save(set);
        }
    }

    private static HashSet<string> Load()
    {
        if (_knownHashes != null) return _knownHashes;

        _knownHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
                if (loaded != null)
                    foreach (var h in loaded) _knownHashes.Add(h);
            }
        }
        catch
        {
            // A corrupt or unreadable file just means placeholders get
            // re-detected empirically the next time ClearPlaceholderCovers
            // runs - not a reason to crash anything that reads this.
        }
        return _knownHashes;
    }

    private static void Save(HashSet<string> set)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(set));
        }
        catch
        {
            // Best-effort - worst case this same hash gets re-added next
            // time it's found instead of staying remembered.
        }
    }
}
