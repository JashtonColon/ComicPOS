using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IPullListService
{
    List<PullListEntry> GetForCustomer(int customerId);
    PullListEntry AddToPullList(int customerId, int seriesId, string preferredCoverLetter);
    void RemoveFromPullList(int entryId);
}
