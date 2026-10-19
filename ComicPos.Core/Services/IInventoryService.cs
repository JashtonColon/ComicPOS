using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IInventoryService
{
    Cover? FindByBarcode(string barcode);
    List<Issue> SearchIssues(string text);
}