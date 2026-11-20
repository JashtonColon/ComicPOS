namespace ComicPos.Core.Models;

public class PullListEntry
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public int SeriesId { get; set; }
    public Series Series { get; set; } = null!;

    public string PreferredCoverLetter { get; set; } = "A";

    // How many copies this customer wants held per issue (gifting,
    // collecting both the regular and a specific variant, etc.) - almost
    // always 1, but shouldn't be hardcoded to it.
    public int Quantity { get; set; } = 1;

    // Free-text instructions for this subscription specifically (e.g.
    // "wants it signed if a signing happens", "ok to hold 2 weeks past
    // FOC", "call before ordering if price is over $6"). Separate from any
    // general customer notes - this is about this one pull, not the person.
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
}
