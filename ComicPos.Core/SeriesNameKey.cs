namespace ComicPos.Core;

// Shared by every place a Series row gets looked up or created by name -
// AutoFetchService's weekly-scrape ingest (AddEntriesAsIssues),
// ImportService's CSV import, and InventoryService's manual
// add/UPC-scan path. All three used to do a plain exact-string match on
// Series.Name independently, which let the same series split into two
// never-merging rows whenever one source called it "Flash" and another
// called it "The Flash" - confirmed live for 9 different series before
// AddEntriesAsIssues alone was fixed to normalize its own lookup key.
// Centralizing the one normalization rule here (strip a leading "The ")
// means every one of those call sites agrees on what "the same series"
// means, instead of each needing its own copy of this logic to stay in
// sync. Only ever used for MATCHING/lookup keys - never changes what a
// Series.Name actually displays as once stored.
public static class SeriesNameKey
{
    public static string Normalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..].Trim()
            : trimmed;
    }
}
