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
            .Where(i => i.FocDate != null && i.FocDate >= today && i.FocDate <= through)
            .ToList();

        var result = new List<FocReportEntry>();

        foreach (var issue in issues)
        {
            var subscribers = _db.PullListEntries
                .Include(p => p.Customer)
                .Where(p => p.IsActive && p.SeriesId == issue.SeriesId)
                .Select(p => new FocReportSubscriber
                {
                    CustomerName = p.Customer.Name,
                    PreferredCover = p.PreferredCoverLetter
                })
                .ToList();

            if (subscribers.Count == 0) continue; // no one wants it - not a buy decision

            result.Add(new FocReportEntry
            {
                SeriesName = issue.Series.Name,
                StartYear = issue.Series.StartYear,
                IssueNumber = issue.IssueNumber,
                FocDate = issue.FocDate,
                Subscribers = subscribers
            });
        }

        return result
            .OrderBy(e => e.FocDate)
            .ThenBy(e => e.SeriesName)
            .ThenBy(e => e.IssueNumber)
            .ToList();
    }
}
