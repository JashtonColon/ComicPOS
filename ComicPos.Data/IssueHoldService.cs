using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class IssueHoldService : IIssueHoldService
{
    private readonly ComicDbContext _db;

    public IssueHoldService(ComicDbContext db)
    {
        _db = db;
    }

    private static string NormalizeCoverLetter(string? input)
    {
        var trimmed = (input ?? "").Trim().ToUpperInvariant();
        return trimmed.Length == 0 ? "A" : trimmed;
    }

    private static int NormalizeQuantity(int quantity) => quantity < 1 ? 1 : quantity;

    public IssueHold CreateHold(int customerId, int issueId, string coverLetter, int quantity = 1, string? notes = null)
    {
        var hold = new IssueHold
        {
            CustomerId = customerId,
            IssueId = issueId,
            CoverLetter = NormalizeCoverLetter(coverLetter),
            Quantity = NormalizeQuantity(quantity),
            Notes = notes,
            CreatedAt = DateTime.Now
        };

        _db.IssueHolds.Add(hold);
        _db.SaveChanges();
        return hold;
    }

    public List<IssueHold> GetOutstandingForIssue(int issueId)
    {
        return _db.IssueHolds
            .Include(h => h.Customer)
            .Where(h => h.IssueId == issueId && !h.IsFulfilled && !h.IsCancelled)
            .OrderBy(h => h.CreatedAt)
            .ToList();
    }

    public List<IssueHold> GetForCustomer(int customerId)
    {
        return _db.IssueHolds
            .Include(h => h.Issue).ThenInclude(i => i.Series)
            .Where(h => h.CustomerId == customerId)
            .OrderByDescending(h => h.CreatedAt)
            .ToList();
    }

    public void FulfillHold(int holdId)
    {
        var hold = _db.IssueHolds.FirstOrDefault(h => h.Id == holdId);
        if (hold == null) return;

        hold.IsFulfilled = true;
        hold.FulfilledAt = DateTime.Now;
        _db.SaveChanges();
    }

    public void CancelHold(int holdId)
    {
        var hold = _db.IssueHolds.FirstOrDefault(h => h.Id == holdId);
        if (hold == null) return;

        hold.IsCancelled = true;
        _db.SaveChanges();
    }
}
