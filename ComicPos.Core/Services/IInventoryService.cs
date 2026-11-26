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

    // Added 2026-11-24, user feedback: "i dont think anything should be in
    // the inventory until we add it ourself" - the plain
    // SearchSeriesForDisplay/GetIssuesForSeries pair above shows EVERY
    // series/issue on file, including the entire Metron/Lunar discovery
    // firehose that's never actually been received (still sitting at 0
    // stock, exactly the clutter that prompted this - a Lunar-discovered
    // issue showed up here with a negative "Total Stock" from the fake-
    // sales seed generator selling a copy nobody ever actually stocked).
    // These two are the Inventory PAGE's own filtered view: only a
    // series/issue with at least one Cover whose StockQuantity is
    // currently > 0 - i.e. something the store has actually received
    // through Add New Item or a restock - shows up here. Deliberately
    // NOT applied to GetAllSeriesForDisplay/SearchSeriesForDisplay
    // themselves, which Customers.razor's "subscribe a customer to a
    // series" picker also uses - pre-subscribing to a series whose first
    // issue hasn't shipped yet is a real, wanted use case (that's the
    // whole point of a pull list), so that picker still needs to see the
    // full catalog, not just what's physically on the shelf.
    //
    // Trade-off worth knowing: this uses CURRENT stock, not "has this ever
    // been received" - there's no separate flag for that (would need a
    // schema migration, and dotnet-ef needs a working shell this machine
    // doesn't currently have - see metron-integration-notes.md). So an
    // issue that sold all the way out drops back off this list until
    // restocked, same as one that was never received at all. For a small
    // shop that's a reasonable reading of "what's in inventory right now"
    // - sold-through history still lives in Dashboard/History/reports -
    // but flag it if a persistent "ever received" marker turns out to
    // matter more than that.
    List<Series> SearchReceivedSeriesForDisplay(string text);
    List<Issue> GetReceivedIssuesForSeries(string seriesName, IssueType type);

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