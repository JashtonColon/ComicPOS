namespace ComicPos.Core.Models;

public class PullListEntry
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public int SeriesId { get; set; }
    public Series Series { get; set; } = null!;

    public string PreferredCoverLetter { get; set; } = "A";

    public bool IsActive { get; set; } = true;
}