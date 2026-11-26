using System.Text.RegularExpressions;

namespace ComicPos.Importers;

// Parses Lunar Distribution's own title grammar - confirmed live against
// a real release file (LunarDistribution_Release_2026-11-25.xlsx,
// inspected 2026-11-24):
//   "WARBIRD #0 (OF 4) 2ND PTG CVR A FRANKY QUITELY"
// i.e. roughly: <SERIES NAME> #<NUM> [(OF N)] [<PRINTING>] [CVR <LETTER>
// [<ARTIST>]] [(RATING)].
//
// This is a DISTINCT grammar from every other title parser already in
// this codebase, so it gets its own rather than reusing one of them:
// CoverLetterParser's shared "Cover\s+([A-Za-z])" regex expects the word
// spelled out, not Lunar's "CVR" abbreviation (changing that shared regex
// to also accept "CVR" would be a smaller diff, but it's shared by
// LocgService and MetronService too, and neither of those sources has
// ever produced a "CVR"-abbreviated string - no reason to touch a
// working shared parser for a format it was never meant to see).
// ComicListService's own patterns expect comiclist.info's distinct
// "(Cover X ...)" parenthesized shape. Distributor catalog text has its
// own long-established abbreviation conventions (CVR, PTG, OF n) that
// don't look like either.
//
// A title with no "#N" in it at all (trade paperbacks, hardcovers,
// omnibuses, box sets) is a collected edition, not something to drop -
// follows the SAME convention ComicListService's Extended Forecast parser
// already uses for the identical case: the whole raw title becomes its
// own one-off "series" name with a placeholder issue number of 1.
// AddEntriesAsIssues' own StripCollectedEditionSuffix check (looking for
// Omnibus/Vol N/TP/HC/GN in the name) then classifies it as
// IssueType.CollectedEdition automatically - no changes needed there at
// all, since Lunar's own TP/HC titles already carry those same words
// ("DC COMPACT COMICS TP BOX SET", "...OMNIBUS HC VOL 02...", "JSA
// COMPENDIUM TP 02", etc., confirmed live). Changed 2026-11-24 after a
// real weekly file came back with 0 new issues added - every row in it
// happened to be exactly this kind of item, and all were silently
// skipped under the old "drop it" behavior.
public static class LunarTitleParser
{
    private static readonly Regex IssueNumberPattern = new(@"#\s*(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex OfMiniseriesMarker = new(@"\(\s*OF\s+\d+\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A volume-year disambiguator right before the "#", e.g. "ABSOLUTE
    // WONDER WOMAN (2024) #26" - common distributor-catalog convention
    // for an ongoing series that's had an earlier volume/relaunch, since
    // "Absolute Wonder Woman" alone wouldn't tell two volumes apart.
    // Stripped out of SeriesName (see ApplyTitle) so a Lunar-sourced
    // series matches the SAME already-existing Series row that Metron/
    // GCD/LOCG created under the plain name with no year suffix -
    // confirmed live as the cause of a Lunar import creating a second,
    // duplicate Series (and therefore what looked like "doubled" old
    // books sitting next to the newly-imported ones) for an ongoing
    // series the store already had on file from another source.
    private static readonly Regex VolumeYearMarker = new(@"\(\s*((?:19|20)\d{2})-?\s*\)\s*$", RegexOptions.Compiled);
    private static readonly Regex PrintingMarker = new(@"\b(\d+)(?:ST|ND|RD|TH)\s+PTG\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoverLetterMarker = new(@"\bCVR\s+([A-Za-z])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // DC/Marvel's own standard term for a reissued classic back issue -
    // "ACTION COMICS #1 FACSIMILE EDITION", "AMAZING FANTASY #15
    // FACSIMILE EDITION", etc. Confirmed live: a facsimile reprint of
    // Action Comics #1 (the real 1938 Superman debut) came in through
    // Lunar and, once the usual "#N" split below ran, produced the exact
    // same bare SeriesName ("Action Comics") and the exact same
    // IssueNumber (1) as the store's real modern ongoing "Action Comics"
    // series - AddEntriesAsIssues' SeriesNameKey match then filed this
    // decades-old one-shot reprint as if it WERE issue #1 of the ongoing
    // series. Same collision class TryLinkAsCollectedEdition's TP/HC/
    // Omnibus suffix already guards against for collected editions; same
    // fix applied here - see ApplyTitle for where this gets folded in.
    private static readonly Regex FacsimileMarker = new(@"\bFACSIMILE(?:\s+EDITION)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Trailing rating/format tags Lunar/Diamond-style catalogs commonly
    // append (mature-readers, adults-only, etc.) - cosmetic only, so
    // they're stripped here rather than ending up tacked onto an
    // artist's name in VariantText.
    private static readonly Regex TrailingTagMarker = new(@"\(\s*(MR|AI|A|O|NOH)\s*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void ApplyTitle(LunarDistributionRow row, string rawTitle)
    {
        row.RawTitle = rawTitle;

        // Checked (and stripped) up front, before the "#N" split below -
        // see FacsimileMarker's own doc comment. Stripping it first means
        // it can never end up stuck inside SeriesName or leftover in
        // VariantText depending on which side of "#N" DC's catalog
        // happens to put it on ("ACTION COMICS #1 FACSIMILE EDITION" vs.
        // "ACTION COMICS FACSIMILE EDITION #1", both seen in the wild) -
        // a single canonical "(Facsimile Edition)" suffix is appended
        // once, below, after SeriesName is otherwise fully resolved.
        var isFacsimile = FacsimileMarker.IsMatch(rawTitle);
        var title = isFacsimile ? FacsimileMarker.Replace(rawTitle, " ") : rawTitle;

        var issueMatch = IssueNumberPattern.Match(title);
        if (!issueMatch.Success)
        {
            // No "#N" at all - treat as a collected edition, not a row to
            // drop (see the class comment above). The whole title is the
            // "series" name and there's no real issue number to carry, so
            // a single placeholder 1 stands in - same as ComicListService.
            row.SeriesName = ApplyFacsimileSuffix(title.Trim(), isFacsimile);
            row.IssueNumber = 1f;
            return;
        }

        if (!float.TryParse(issueMatch.Groups[1].Value, out var issueNumber))
            return;

        row.IssueNumber = issueNumber;
        row.SeriesName = Regex.Replace(title.Substring(0, issueMatch.Index), @"\s+", " ").Trim();

        var yearMatch = VolumeYearMarker.Match(row.SeriesName);
        if (yearMatch.Success)
        {
            row.SeriesStartYearHint = int.Parse(yearMatch.Groups[1].Value);
            row.SeriesName = row.SeriesName.Substring(0, yearMatch.Index).Trim();
        }

        if (row.SeriesName.Length == 0)
        {
            row.SeriesName = null;
            row.IssueNumber = null;
            return;
        }

        row.SeriesName = ApplyFacsimileSuffix(row.SeriesName, isFacsimile);

        var remainder = title.Substring(issueMatch.Index + issueMatch.Length);
        remainder = OfMiniseriesMarker.Replace(remainder, " ");

        var printingMatch = PrintingMarker.Match(remainder);
        if (printingMatch.Success)
        {
            var n = printingMatch.Groups[1].Value;
            row.PrintingLabel = $"{n}{OrdinalSuffix(n)} Printing";
            remainder = remainder.Remove(printingMatch.Index, printingMatch.Length);
        }

        var coverMatch = CoverLetterMarker.Match(remainder);
        if (coverMatch.Success)
        {
            row.CoverLetter = coverMatch.Groups[1].Value.ToUpperInvariant();
            remainder = remainder.Substring(coverMatch.Index + coverMatch.Length);
        }

        remainder = TrailingTagMarker.Replace(remainder, " ");
        remainder = Regex.Replace(remainder, @"\s+", " ").Trim(' ', '-', ',');
        if (remainder.Length > 0)
            row.VariantText = remainder;
    }

    // Applied once SeriesName is otherwise fully resolved, on both the
    // "#N" and "no #N" paths - see FacsimileMarker's own doc comment for
    // why this gets its own distinct series rather than merging into
    // whatever ongoing series happens to share the same bare name.
    private static string ApplyFacsimileSuffix(string seriesName, bool isFacsimile) =>
        isFacsimile ? $"{seriesName} (Facsimile Edition)" : seriesName;

    private static string OrdinalSuffix(string digits)
    {
        // Printings realistically never go past the low single digits,
        // but this follows the general English ordinal rule correctly
        // anyway (11th/12th/13th are the one exception to "last digit
        // decides").
        if (!int.TryParse(digits, out var n)) return "th";
        if (n % 100 is 11 or 12 or 13) return "th";
        return (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
    }
}
