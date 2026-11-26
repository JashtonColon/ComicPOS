namespace ComicPos.Importers;

// One row of a Lunar Distribution weekly release Excel file - one specific
// cover/printing of one issue. RawTitle is kept as-is; SeriesName/
// IssueNumber/CoverLetter/PrintingLabel/VariantText are parsed out of it
// by LunarTitleParser - see that class for the title grammar this
// assumes. Unlike CsvSolicitationParser's SolicitationRow, every other
// field here comes straight off Lunar's own named columns (no manual CSV-
// authoring step needed), plus ImageBytes - the product's own real cover
// photo, embedded directly in the workbook right next to its row (see
// LunarDistributionParser) - nothing here is scraped, estimated, or
// downloaded from a rate-limited/Cloudflare-guarded site.
public class LunarDistributionRow
{
    public string RawTitle { get; set; } = string.Empty;

    // Null for anything LunarTitleParser couldn't find a "#N" in (trade
    // paperbacks, hardcovers, omnibuses, non-comic merchandise) -
    // LunarDistributionParser logs these as skipped rather than guessing
    // at them, same spirit as ComicListService silently skipping the
    // same kinds of lines from comiclist.info.
    public string? SeriesName { get; set; }
    public float? IssueNumber { get; set; }
    public string CoverLetter { get; set; } = "A";

    // The volume year when Lunar's own title included one as a
    // disambiguator right before the "#" - e.g. "ABSOLUTE WONDER WOMAN
    // (2024) #26" (a standard distributor-catalog convention for an
    // ongoing series that's been relaunched/renumbered before, so plain
    // "Absolute Wonder Woman" is ambiguous on its own). LunarTitleParser
    // strips this parenthetical OUT of SeriesName (so it matches the
    // same series as already created by Metron/GCD/LOCG, none of which
    // include a year in the name) and puts the year here instead - see
    // ComicListEntry.SeriesStartYearHint, which this feeds directly.
    // Null when the title had no such marker.
    public int? SeriesStartYearHint { get; set; }

    // Set when the title carried a printing marker ("2ND PTG", "3RD
    // PTG"). Shown in the Covers grid via Cover.CoverArtist exactly like
    // an LOCG variant's free-text label. Also what keeps a reprint from
    // silently colliding with the original Cover A slot when both parse
    // to the same nominal letter - AutoFetchService.ResolveCoverLetters
    // already reassigns ANY letter collision within one batch to its own
    // free slot, so this doesn't need special "treat as a guess"
    // handling, just a distinct barcode (which Lunar always provides) and
    // a label worth showing.
    public string? PrintingLabel { get; set; }

    // Whatever text followed "CVR X" in the title, once the issue number,
    // "(OF N)" marker, printing marker, and the cover letter itself are
    // all stripped out - usually the variant cover's artist name
    // ("FRANKY QUITELY"). Used as Cover.CoverArtist only when there's no
    // PrintingLabel to show instead (see AutoFetchService.
    // ImportLunarDistributionRelease).
    public string? VariantText { get; set; }

    public string Publisher { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal? Price { get; set; }

    // Lunar's own real UPC when the row has one, falling back to
    // "LUN-{ProductCode}" (Lunar's own stable internal SKU) when it
    // doesn't - see LunarDistributionParser. Either way this is a real,
    // stable per-product identifier straight from the distributor, never
    // a randomly-generated placeholder.
    public string? Upc { get; set; }

    public DateOnly? FocDate { get; set; }
    public DateOnly? ReleaseDate { get; set; }

    // The product's own cover photo, read directly out of the workbook
    // (the embedded picture anchored to this row) - never a URL, since
    // there's nothing to download here; Lunar ships the image itself
    // right alongside the data. Null when this particular row had no
    // image anchored to it.
    public byte[]? ImageBytes { get; set; }
}
