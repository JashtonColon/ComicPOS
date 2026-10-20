namespace ComicPos.Core.Models;

public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }

    public bool IsActive { get; set; } = true;

    public List<PullListEntry> PullListEntries { get; set; } = new();
}