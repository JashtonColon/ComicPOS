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

    // Normalizes whatever the UI sends into something that can't corrupt a
    // row: a blank/invalid cover always falls back to "A" rather than ever
    // being saved empty, and quantity can never go below 1 (a 0 or
    // negative "pull quantity" would silently drop the customer from the
    // FOC buy report with nothing to explain why).
    private static string NormalizeCoverLetter(string? input)
    {
        var trimmed = (input ?? "").Trim().ToUpperInvariant();
        return trimmed.Length == 0 ? "A" : trimmed;
    }

    private static int NormalizeQuantity(int quantity) => quantity < 1 ? 1 : quantity;

    // Idempotent: re-adding a series that was previously removed just
    // reactivates the existing row (and updates the cover/quantity/notes)
    // instead of creating a duplicate entry.
    public PullListEntry AddToPullList(int customerId, int seriesId, string preferredCoverLetter, int quantity = 1, string? notes = null)
    {
        var cover = NormalizeCoverLetter(preferredCoverLetter);
        var qty = NormalizeQuantity(quantity);

        var existing = _db.PullListEntries
            .FirstOrDefault(p => p.CustomerId == customerId && p.SeriesId == seriesId);

        if (existing != null)
        {
            existing.IsActive = true;
            existing.PreferredCoverLetter = cover;
            existing.Quantity = qty;
            existing.Notes = notes;
            _db.SaveChanges();
            return existing;
        }

        var entry = new PullListEntry
        {
            CustomerId = customerId,
            SeriesId = seriesId,
            PreferredCoverLetter = cover,
            Quantity = qty,
            Notes = notes,
            IsActive = true
        };

        _db.PullListEntries.Add(entry);
        _db.SaveChanges();
        return entry;
    }

    public void UpdatePullListEntry(int entryId, string preferredCoverLetter, int quantity, string? notes)
    {
        var entry = _db.PullListEntries.FirstOrDefault(p => p.Id == entryId);
        if (entry == null) return;

        entry.PreferredCoverLetter = NormalizeCoverLetter(preferredCoverLetter);
        entry.Quantity = NormalizeQuantity(quantity);
        entry.Notes = notes;
        _db.SaveChanges();
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
