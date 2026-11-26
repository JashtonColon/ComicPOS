using System.Text.RegularExpressions;

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
//
// Widened 2026-11-24 after a real Lunar backfill run split two more
// already-existing series in half: Lunar's own all-caps catalog style
// dropped a subtitle separator the "proper" title-cased name (from
// Metron/GCD/a manual add) keeps - "BARBARA GORDON BREAKOUT (2026)" vs
// the already-existing "Barbara Gordon: Breakout (2026)", and
// "BATMAN & ROBIN YEAR ONE DYNAMIC DUOS (2026)" vs "Batman & Robin: Year
// One - Dynamic Duos (2026)" - each pair differs ONLY by a colon and/or
// a dash-as-separator plus the whitespace around it, never by anything
// that could mean two deliberately-different volumes (unlike the Lunar
// "(YYYY)" case, which IS a real disambiguating signal sometimes and is
// deliberately NOT touched here - see LunarTitleParser.VolumeYearMarker/
// ComicListEntry.SeriesStartYearHint, which strip that out of the title
// before it ever reaches this method, rather than normalizing it away at
// the matching-key level). A dash is only treated as separator noise
// when it has whitespace on both sides (" - "/" – "/" — "), so a real
// compound-word title like "Spider-Man" or "X-23" is left untouched.
public static class SeriesNameKey
{
    private static readonly Regex SubtitleSeparator = new(@":|\s[-‐-―]\s", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[4..].Trim();

        trimmed = SubtitleSeparator.Replace(trimmed, " ");
        return Whitespace.Replace(trimmed, " ").Trim();
    }
}
