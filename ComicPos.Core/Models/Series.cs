namespace ComicPos.Core.Models;

public class Series{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public List<Issue> Issues { get; set; } = new();
}