using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class FocReportService : IFocReportService
{
    private readonly ComicDbContext _db;

    public FocReportService(ComicDbContext db)
    {
        _db = db;
    }

    public List<FocReportEntry> GetUpcomingFocReport(int daysAhead)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var through = today.AddDays(daysAhead);

        var issues = _db.Issues
            .Include(i => i.Series)
            .Include(i => i.Covers)
            .Where(i => i.FocDate != null && i.FocDate >= today && i.FocDate <= through)
            .ToList();

        var result = new List<FocReportEntry>();

        foreach (var issue in issues)
        {
            var availableCovers = issue.Covers
                .Select(c => c.CoverLetter.Trim().ToUpperInvariant())
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            var subscribers = _db.PullListEntries
                .Include(p => p.Customer)
                .Where(p => p.IsActive && p.SeriesId == issue.SeriesId)
                .ToList()
                .Select(p => new FocReportSubscriber
                {
                    CustomerName = p.Customer.Name,
                    PreferredCover = p.PreferredCoverLetter,
                    Quantity = p.Quantity,
                    // No covers on file at all (not received/entered yet)
                    // isn't a real mismatch - nothing to compare against -
                    // so only flag it once this issue actually has covers.
                    CoverIsAvailable = availableCovers.Count == 0
                        || availableCovers.Contains(p.PreferredCoverLetter.Trim().ToUpperInvariant())
                })
                .ToList();

            if (subscribers.Count == 0) continue; // no one wants it - not a buy decision

            result.Add(new FocReportEntry
            {
                SeriesName = issue.Series.Name,
                StartYear = issue.Series.StartYear,
                IssueNumber = issue.IssueNumber,
                FocDate = issue.FocDate,
                Subscribers = subscribers,
                AvailableCoverLetters = availableCovers
            });
        }

        return result
            .OrderBy(e => e.FocDate)
            .ThenBy(e => e.SeriesName)
            .ThenBy(e => e.IssueNumber)
            .ToList();
    }
}
