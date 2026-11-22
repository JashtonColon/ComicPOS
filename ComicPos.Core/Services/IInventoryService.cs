using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IInventoryService
{
    Cover? FindByBarcode(string barcode);
    List<Issue> SearchIssues(string text);
    List<string> GetAllSeriesNames();
    List<Issue> GetIssuesForSeries(string seriesName, IssueType type);
    List<Series> GetAllSeriesForDisplay();
    List<Series> SearchSeriesForDisplay(string text);

    // Exact-name lookup for when a screen already has a series' display
    // name (e.g. from the Inventory drill-down) and needs its real Id -
    // for example to look up who has it on their pull list.
    Series? GetSeriesByName(string name);

    // Every distinct cover letter seen across every issue this store has
    // on file for a series (single issues and collected editions alike) -
    // so a "which cover do you want" field can show real options instead
    // of a blind free-text box with no idea what this series actually
    // comes in.
    List<string> GetKnownCoverLetters(int seriesId);
    void UpdateCover(Cover cover);

    // Call after UpdateCover (or any other save through this service)
    // throws, before trying another save - EF Core's ChangeTracker does
    // NOT roll back a failed SaveChanges on its own, so the entity stays
    // tracked as "Modified"/"Added" and would get silently flushed into
    // whatever save call happens to succeed next (same root cause as the
    // duplicate-row bug fixed in AutoFetchService).
    void ClearFailedChanges();

    // Manual single-item entry point for the Inventory tab's "Add New Item"
    // button - finds-or-creates the Series and Issue (same matching rules
    // as the CSV importer: by name for Series, by Series+IssueNumber+Type
    // for Issue), then finds-or-creates the Cover by barcode. Throws
    // InvalidOperationException if the barcode already exists under a
    // different issue (the unique-barcode index would reject that anyway),
    // so the form can show a clear message instead of a raw DB exception.
    Cover AddOrUpdateItem(
        string seriesName,
        int startYear,
        float issueNumber,
        IssueType type,
        string publisher,
        DateOnly? focDate,
        DateOnly? releaseDate,
        string coverLetter,
        string barcode,
        decimal price,
        int stockQuantity,
        string? description,
        string author = "",
        string penciler = "",
        string inker = "",
        string colorist = "",
        string letterer = "");
}