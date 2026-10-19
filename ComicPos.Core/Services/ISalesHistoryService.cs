    using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface ISalesHistoryService
{
    List<SaleLine> GetHistoryForSeries(string seriesNameSearch);
}