using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface ISalesHistoryService
{
    List<SaleLine> GetHistoryForSeries(string seriesNameSearch);
    List<string> GetSeriesWithSales();
    List<Issue> GetIssuesWithSales(string seriesName);
}