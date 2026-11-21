using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IIssueHoldService
{
    IssueHold CreateHold(int customerId, int issueId, string coverLetter, int quantity = 1, string? notes = null);

    // Every outstanding (not yet fulfilled or cancelled) hold on a given
    // issue - for a "who's holding this exact issue" view, same spirit as
    // IPullListService.GetForSeries but at the single-issue level.
    List<IssueHold> GetOutstandingForIssue(int issueId);

    // A customer's own hold history - outstanding and past - so a
    // customer's detail screen can show what they've got waiting for
    // them without needing to dig through every issue individually.
    List<IssueHold> GetForCustomer(int customerId);

    void FulfillHold(int holdId);
    void CancelHold(int holdId);
}
