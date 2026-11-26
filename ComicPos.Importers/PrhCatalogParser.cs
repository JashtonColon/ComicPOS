using ClosedXML.Excel;
using ComicPos.Core.Models;

namespace ComicPos.Importers;

public class PrhCatalogParseResult
{
    public List<PrhCatalogRow> Rows { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

// Reads PRH's "Comics Retail" solicitation spreadsheet - see
// PrhCatalogRow for what each row carries and PrhTitleParser for how
// Title gets split apart. Confirmed live against a real file
// (PRH-Comics-Retail_11-25-26.xlsx, inspected 2026-11-25 via Python/
// openpyxl before this C# version existed): single sheet, a "Catalog
// <Month> <Year>" title in row 1, the real named columns in row 2 (ISBN/
// UPC, Quantity, Title, Subtitle, Creators, Imprint, Retail Price (US),
// Retail Price (CAN), Format, FOC Date, On-Sale Date, Series Frequency,
// Age, Grade) - matched by header text, same spirit as every other
// parser here. Every cell in the file is plain text, including both
// date columns (MM/dd/yyyy as a STRING, never a real Excel date value)
// and even Quantity - so every column is read as a string first and
// parsed from there, never via ClosedXML's typed cell accessors.
//
// Same ClosedXML choice as LunarDistributionParser, for the same
// license reason - see that class's own doc comment.
public static class PrhCatalogParser
{
    private static readonly string[] RequiredColumns = { "Title", "Format" };

    public static PrhCatalogParseResult Parse(string filePath)
    {
        var result = new PrhCatalogParseResult();

        if (!File.Exists(filePath))
        {
            result.Errors.Add($"File not found: {filePath}");
            return result;
        }

        using var workbook = new XLWorkbook(filePath);
        var ws = workbook.Worksheets.First();

        // The real header isn't necessarily row 1 - PRH's own export
        // puts a "Catalog <Month> <Year>" title in row 1 and the real
        // named columns in row 2 (confirmed live). Scanning for whichever
        // of the first few rows actually contains a "Title" column,
        // rather than assuming a fixed row number, means this keeps
        // working if PRH ever drops that title row - same spirit as
        // matching columns by header TEXT rather than position.
        IXLRow? headerRow = null;
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateRow in ws.RowsUsed().Take(5))
        {
            var candidateIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in candidateRow.CellsUsed())
            {
                var header = cell.GetString().Trim();
                if (!string.IsNullOrWhiteSpace(header) && !candidateIndex.ContainsKey(header))
                    candidateIndex[header] = cell.Address.ColumnNumber;
            }
            if (candidateIndex.ContainsKey("Title"))
            {
                headerRow = candidateRow;
                columnIndex = candidateIndex;
                break;
            }
        }

        if (headerRow == null)
        {
            result.Errors.Add("Couldn't find a header row with a \"Title\" column in the first 5 rows. Is this really a PRH Comics Retail spreadsheet?");
            return result;
        }

        foreach (var required in RequiredColumns)
        {
            if (!columnIndex.ContainsKey(required))
            {
                result.Errors.Add($"Missing required column: {required}. Is this really a PRH Comics Retail spreadsheet?");
                return result;
            }
        }

        string GetText(IXLRow xlRow, string column)
        {
            if (!columnIndex.TryGetValue(column, out var col)) return "";
            return xlRow.Cell(col).GetString().Trim();
        }

        decimal? GetPrice(IXLRow xlRow, string column)
        {
            var text = GetText(xlRow, column);
            if (text.Length == 0) return null;
            // "$60.00 US" / "$9.99 US" - always this shape, confirmed
            // live across every row in the sample file.
            var digits = text.Replace("$", "").Replace("US", "").Replace("CAN", "").Trim();
            return decimal.TryParse(digits, out var d) ? d : null;
        }

        DateOnly? GetDate(IXLRow xlRow, string column)
        {
            var text = GetText(xlRow, column);
            return DateOnly.TryParseExact(text, "MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();
        for (int r = headerRow.RowNumber() + 1; r <= lastRow; r++)
        {
            var xlRow = ws.Row(r);
            var title = GetText(xlRow, "Title");
            if (string.IsNullOrWhiteSpace(title)) continue; // blank spacer row

            var format = GetText(xlRow, "Format").ToUpperInvariant();

            var row = new PrhCatalogRow();
            PrhTitleParser.ApplyTitle(row, title);

            // CB ("Comic Book") is the only single-issue format PRH uses
            // in this feed - TR/HC/NT/DG (trade paperback, hardcover, and
            // whatever else PRH's own catalog mixes in) are all one-off
            // collected items. Read straight off PRH's own column rather
            // than guessed from the name - see PrhCatalogRow.TypeHint's
            // own doc comment for why that matters here specifically. An
            // unrecognized/blank format defaults to SingleIssue rather
            // than silently mislabeling it the other way, since CB is
            // the more common case in this feed (118 of 386 rows in the
            // sample file) and a wrongly-CollectedEdition single issue is
            // a more visible, easier-to-notice mistake in Inventory's
            // browse-by-type screen than the reverse.
            row.TypeHint = format == "CB" || format.Length == 0
                ? IssueType.SingleIssue
                : IssueType.CollectedEdition;

            row.Publisher = GetText(xlRow, "Imprint").ToUpperInvariant();
            var creators = GetText(xlRow, "Creators");
            row.Creators = creators.Length > 0 ? creators : null;
            row.Price = GetPrice(xlRow, "Retail Price (US)");
            row.FocDate = GetDate(xlRow, "FOC Date");
            row.ReleaseDate = GetDate(xlRow, "On-Sale Date");

            var isbn = GetText(xlRow, "ISBN/UPC");
            row.Upc = isbn.Length > 0 ? isbn : null;

            result.Rows.Add(row);
        }

        return result;
    }
}
