using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class SalesHistoryService : ISalesHistoryService
{
    private readonly ComicDbContext _db;

    public SalesHistoryService(ComicDbContext db)
    {
        _db = db;
    }

    public List<SaleLine> GetHistoryForSeries(string seriesNameSearch)
    {
        return _db.SaleLines
            .Include(l => l.Cover).ThenInclude(c => c.Issue).ThenInclude(i => i.Series)
            .Include(l => l.Sale)
            .Where(l => EF.Functions.Like(l.Cover.Issue.Series.Name, $"%{seriesNameSearch}%"))
            .OrderByDescending(l => l.Sale.SoldAt)
            .ToList();
    }

    public List<string> GetSeriesWithSales()
    {
        return _db.SaleLines
            .Select(l => l.Cover.Issue.Series.Name)
            .Distinct()
            .ToList();
    }

    public List<Issue> GetIssuesWithSales(string seriesName)
    {
        return _db.SaleLines
            .Where(l => l.Cover.Issue.Series.Name == seriesName)
            .Select(l => l.Cover.Issue)
            .Distinct()
            .ToList();
    }

    public List<SaleLine> GetSalesForIssue(int issueId)
    {
        return _db.SaleLines
            .Include(l => l.Cover).ThenInclude(c => c.Issue).ThenInclude(i => i.Series)
            .Include(l => l.Sale)
            .Where(l => l.Cover.IssueId == issueId)
            .OrderByDescending(l => l.Sale.SoldAt)
            .ToList();
    }
}