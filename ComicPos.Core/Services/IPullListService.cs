using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IPullListService
{
    List<PullListEntry> GetForCustomer(int customerId);

    // Idempotent: adding a series already on the customer's (active or
    // previously-removed) pull list updates the existing row in place
    // instead of creating a duplicate.
    PullListEntry AddToPullList(int customerId, int seriesId, string preferredCoverLetter, int quantity = 1, string? notes = null);

    // Changes an existing entry's cover/quantity/notes without touching
    // IsActive - for correcting a subscription (wrong cover, customer now
    // wants 2 copies) without a destructive remove-then-re-add round trip,
    // which used to lose the row's Id and any history tied to it.
    void UpdatePullListEntry(int entryId, string preferredCoverLetter, int quantity, string? notes);

    void RemoveFromPullList(int entryId);
}
