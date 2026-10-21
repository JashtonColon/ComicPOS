using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IInventoryService
{
    Cover? FindByBarcode(string barcode);
    List<Issue> SearchIssues(string text);
    List<string> GetAllSeriesNames();
    List<Issue> GetIssuesForSeries(string seriesName);
    List<Series> GetAllSeriesForDisplay();
    List<Issue> GetIssuesForSeries(string seriesName, IssueType type);
}