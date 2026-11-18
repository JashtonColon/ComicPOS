using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class PullListService : IPullListService
{
    private readonly ComicDbContext _db;

    public PullListService(ComicDbContext db)
    {
        _db = db;
    }

    public List<PullListEntry> GetForCustomer(int customerId)
    {
        return _db.PullListEntries
            .Include(p => p.Series)
            .Where(p => p.CustomerId == customerId && p.IsActive)
            .OrderBy(p => p.Series.Name)
            .ToList();
    }

    // Idempotent: re-adding a series that was previously removed just
    // reactivates the existing row (and updates the cover preference)
    // instead of creating a duplicate entry.
    public PullListEntry AddToPullList(int customerId, int seriesId, string preferredCoverLetter)
    {
        var existing = _db.PullListEntries
            .FirstOrDefault(p => p.CustomerId == customerId && p.SeriesId == seriesId);

        if (existing != null)
        {
            existing.IsActive = true;
            existing.PreferredCoverLetter = preferredCoverLetter;
            _db.SaveChanges();
            return existing;
        }

        var entry = new PullListEntry
        {
            CustomerId = customerId,
            SeriesId = seriesId,
            PreferredCoverLetter = preferredCoverLetter,
            IsActive = true
        };

        _db.PullListEntries.Add(entry);
        _db.SaveChanges();
        return entry;
    }

    // Soft delete, same pattern as Customer.IsActive, so history/audit isn't lost.
    public void RemoveFromPullList(int entryId)
    {
        var entry = _db.PullListEntries.FirstOrDefault(p => p.Id == entryId);
        if (entry != null)
        {
            entry.IsActive = false;
            _db.SaveChanges();
        }
    }
}
