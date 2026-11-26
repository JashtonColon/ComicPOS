using System.Text.RegularExpressions;

namespace ComicPos.Importers;

// Parses PRH's own title grammar - confirmed live against a real
// "Comics Retail" spreadsheet (PRH-Comics-Retail_11-25-26.xlsx, inspected
// 2026-11-25). Unlike Lunar Distribution (one consistent "CVR X"
// abbreviation throughout its own feed), PRH's Title column mixes
// several different PUBLISHERS' own catalog-text conventions side by
// side in the same file - confirmed live, all of the below seen in the
// same spreadsheet:
//   "Minor Threats: The Cleaner #4 (CVR A) (Scott Hepburn)"
//   "Arcadia #12 Cover A Inaki Miranda"                       (no parens at all)
//   "X-MEN #46 COVER A"                                       (all-caps, no artist)
//   "Institute one-shot (CVR A) (Mike del Mundo) (3W3M)"      (no "#N" at all)
//   "Amphibia: Strange Voyage, Issue #4"                      (no cover marker at all)
// and a trade/hardcover/omnibus title often carries one of PRH's own
// bracketed distribution-channel tags instead of any cover info:
//   "Amazing Spider-Man: Power And Responsibility [Marvel Brick 1]"
//   "X-Men: Blue & Gold - Fatal Attractions Omnibus Greg Capullo Cover [DM Only]"
// So this accepts BOTH "CVR" and the spelled-out "Cover" before a cover
// letter - LunarTitleParser deliberately only accepts "CVR" (see its own
// doc comment) because Lunar's own feed never spells it out; PRH's does,
// mixed with the abbreviation, in the same column.
//
// Collected-edition-ness is read off PRH's own Format column by
// PrhCatalogParser (see PrhCatalogRow.TypeHint), NOT guessed from the
// title the way AddEntriesAsIssues' StripCollectedEditionSuffix has to
// for every other source - PRH's own trade/hardcover titles are too
// inconsistent about carrying a recognizable "Omnibus"/"TP"/"HC" word
// for that to be reliable here (see the Spider-Man example above). This
// parser's only job is pulling SeriesName/IssueNumber/CoverLetter/
// VariantText apart - a true one-shot comic book (Format = CB, often
// genuinely no "#N" anywhere in its title) and a one-off collected
// edition both fall back to the same IssueNumber = 1 placeholder when
// there's no "#N" to find, same spirit as LunarTitleParser's own "no #N"
// fallback - which one an issue actually IS comes from TypeHint, set
// separately by the caller from the Format column, not from anything
// this class decides.
public static class PrhTitleParser
{
    private static readonly Regex IssueNumberPattern = new(@"#\s*(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex OneShotMarker = new(@"\bone[\s-]?shot\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoverLetterMarker = new(@"\b(?:CVR|Cover)\s+([A-Za-z])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingFormatTag = new(@"\s*\[([^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex TrailingIssueWord = new(@",?\s*Issue\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OrphanLeadingCloseParen = new(@"^\)\s*", RegexOptions.Compiled);

    public static void ApplyTitle(PrhCatalogRow row, string rawTitle)
    {
        row.RawTitle = rawTitle;
        var title = rawTitle;

        // PRH's own distribution-channel tag ("[DM Only]", "[Marvel
        // Compact]", "[Marvel Brick 1]", "[New Printing 2, DM Only]") -
        // never part of the actual published title, so it's pulled off
        // the end and kept as a note (VariantText) rather than silently
        // discarded or left stuck onto SeriesName.
        string? formatTag = null;
        var tagMatch = TrailingFormatTag.Match(title);
        if (tagMatch.Success)
        {
            formatTag = tagMatch.Groups[1].Value.Trim();
            title = title.Substring(0, tagMatch.Index).TrimEnd();
        }

        var issueMatch = IssueNumberPattern.Match(title);
        string remainder;
        if (issueMatch.Success && float.TryParse(issueMatch.Groups[1].Value, out var issueNumber))
        {
            row.IssueNumber = issueNumber;
            row.SeriesName = title.Substring(0, issueMatch.Index).Trim();
            remainder = title.Substring(issueMatch.Index + issueMatch.Length);
        }
        else
        {
            // No "#N" - either a true one-shot ("Institute one-shot
            // (CVR A) ...") or a collected edition with no issue number
            // at all. Either way IssueNumber = 1; see this method's own
            // doc comment for why that's fine for both cases.
            var oneShotMatch = OneShotMarker.Match(title);
            row.IssueNumber = 1f;
            if (oneShotMatch.Success)
            {
                row.SeriesName = title.Substring(0, oneShotMatch.Index).Trim();
                remainder = title.Substring(oneShotMatch.Index + oneShotMatch.Length);
            }
            else
            {
                row.SeriesName = title.Trim();
                remainder = "";
            }
        }

        row.SeriesName = TrailingIssueWord.Replace(row.SeriesName ?? "", "").Trim(' ', '-', ':');
        if (string.IsNullOrWhiteSpace(row.SeriesName))
            row.SeriesName = title.Trim();

        var coverMatch = CoverLetterMarker.Match(remainder);
        if (coverMatch.Success)
        {
            row.CoverLetter = coverMatch.Groups[1].Value.ToUpperInvariant();
            remainder = remainder.Substring(coverMatch.Index + coverMatch.Length);
            // The cover marker's own wrapping "(" is now gone from
            // remainder (it was part of what got sliced off above),
            // which would otherwise leave its matching ")" orphaned at
            // the very front of whatever comes next - e.g. "(CVR A)
            // (Mike del Mundo)" becomes ") (Mike del Mundo)" once "CVR
            // A" itself is removed. Stripped here rather than with a
            // blanket paren trim below, which would just as easily eat
            // into an unrelated, still-balanced "(3W3M)" group later in
            // the same remainder.
            remainder = OrphanLeadingCloseParen.Replace(remainder.TrimStart(), "");
        }

        remainder = Regex.Replace(remainder, @"\s+", " ").Trim(' ', '-', ',');

        var variantParts = new List<string>();
        if (remainder.Length > 0) variantParts.Add(remainder);
        if (formatTag != null) variantParts.Add(formatTag);
        row.VariantText = variantParts.Count > 0 ? string.Join(" - ", variantParts) : null;
    }
}
