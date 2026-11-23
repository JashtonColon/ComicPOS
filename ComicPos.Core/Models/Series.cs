namespace ComicPos.Core.Models;

public class Series{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public int StartYear { get; set; }
    public List<Issue> Issues { get; set; } = new();

    // Links this series to a specific Comic Vine "volume" so new-issue
    // checks don't have to re-search by name (which is ambiguous - lots of
    // series share a title across publishers/reboots). Null = not linked yet.
    public int? ComicVineVolumeId { get; set; }
    public DateTime? LastCheckedForNewIssues { get; set; }

    // Metron's own numeric id for this exact volume - the most precise
    // identity this app has for "which real series is this" (no name or
    // year heuristic involved at all, unlike ComicVineVolumeId's own
    // matching path via PickBestVolumeMatch). Set the moment an entry
    // discovered through Metron resolves to this Series (new or already
    // existing), and preferred over name-based lookup in
    // AutoFetchService.AddEntriesAsIssues from then on. Null for a series
    // that predates Metron discovery, or came in through a source that
    // doesn't carry this (LOCG/ComicList/GCD/Comicade/manual add).
    public int? MetronSeriesId { get; set; }

    // When TryAutoLink last attempted (successfully or not) to match this
    // series to a Comic Vine volume. Many series created from ComicList/
    // Extended Forecast are trade paperbacks, hardcovers, graphic novels,
    // or other one-shots (see ComicListService) that structurally can
    // never match a Comic Vine "volume" (Comic Vine only tracks ongoing
    // single-issue series) - without this, every one of those would burn
    // a fresh Comic Vine API call on every single Check Now run, forever,
    // for a lookup that will never succeed. Null = never attempted yet.
    public DateTime? LastAutoLinkAttempt { get; set; }
}
