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
}