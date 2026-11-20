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
