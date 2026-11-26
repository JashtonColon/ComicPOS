using ComicPos.Core.Models;

namespace ComicPos.Importers;

// One row of PRH's "Comics Retail" solicitation spreadsheet - the clean,
// already-tabular sibling of the PRH Panels PDF catalog PRH also
// publishes (same underlying solicitation data, but no page-layout
// reconstruction needed at all here: ISBN/UPC, Title, Creators, Imprint,
// both US/CAN retail prices, Format, FOC Date, and On-Sale Date all
// arrive as their own named columns, confirmed live against a real file,
// PRH-Comics-Retail_11-25-26.xlsx, inspected 2026-11-25). This is PRH's
// FULL Direct Market distribution list, not just Penguin Random House's
// own imprints - Marvel, DC, Dark Horse, IDW, Oni Press, Kodansha, Seven
// Seas, Titan Books and many more all showed up in the same file - so
// one spreadsheet here can cover a large share of a store's monthly
// orders. See PrhCatalogParser for how a row gets read and
// PrhTitleParser for how Title gets split apart.
//
// No embedded cover image (unlike LunarDistributionRow) - this is a pure
// data feed, not a product-photo export. A newly-imported series/issue
// here gets its cover the same way a Lunar-imported one with no photo,
// or any Metron/LOCG/GCD-sourced one, already does: AutoFetchService's
// existing Comic Vine auto-link/backfill pass.
public class PrhCatalogRow
{
    public string RawTitle { get; set; } = string.Empty;
    public string? SeriesName { get; set; }
    public float? IssueNumber { get; set; }
    public string CoverLetter { get; set; } = "A";

    // Whatever's left once the issue number and a recognized cover-
    // letter marker ("CVR A" or "Cover A" - PRH's own feed uses both
    // spellings, confirmed live) are both pulled out - usually an artist
    // name, a rarity-variant note ("Variant RI (100) Foil"), or one of
    // PRH's own bracketed distribution-channel tags ("DM Only", "Marvel
    // Compact"). Shown as Cover.CoverArtist, same as Lunar's VariantText
    // - see PrhTitleParser.
    public string? VariantText { get; set; }

    public string Publisher { get; set; } = string.Empty;

    // PRH's own combined credits line ("Story written by X, Art by Y;
    // Curated and Edited by Z") - kept as one plain string rather than
    // split into Author/Penciler/Inker/Colorist/Letterer. Splitting it
    // reliably would need its own role-parsing grammar on top of
    // everything else here, for a field that's only ever shown as
    // descriptive text in the New Books detail popup - not worth the
    // risk of silently mis-assigning a role for what it would buy.
    public string? Creators { get; set; }

    public decimal? Price { get; set; }
    public string? Upc { get; set; }
    public DateOnly? FocDate { get; set; }
    public DateOnly? ReleaseDate { get; set; }

    // Straight off PRH's own Format column (CB/TR/HC/NT/DG) - a far more
    // reliable signal than guessing collected-edition-ness from the name
    // the way AddEntriesAsIssues' own StripCollectedEditionSuffix has to
    // for every other source, since PRH's naming is inconsistent about
    // including a recognizable "Omnibus"/"Vol. N"/"TP"/"HC" marker at all
    // (e.g. "Amazing Spider-Man: Power And Responsibility [Marvel Brick
    // 1]" is a trade paperback with none of those words anywhere in it).
    // See ComicListEntry.TypeHint, which this feeds directly.
    public IssueType TypeHint { get; set; } = IssueType.SingleIssue;
}
