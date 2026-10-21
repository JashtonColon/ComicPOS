namespace ComicPos.Core.Models;

public class Series{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public int StartYear { get; set; }
    public List<Issue> Issues { get; set; } = new();
}