using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;

namespace ComicPos.Importers;

public class LunarParseResult
{
    public List<LunarDistributionRow> Rows { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

// Reads a Lunar Distribution weekly release Excel file - see
// LunarDistributionRow for what each row carries and LunarTitleParser for
// how Title gets broken apart. Confirmed live against a real file
// (LunarDistribution_Release_2026-11-25.xlsx, inspected 2026-11-24 via
// Python/openpyxl before this C# version existed): single sheet, one
// header row, named columns (any order - matched by header text, same
// spirit as CsvSolicitationParser), and roughly one embedded cover photo
// per data row, anchored to that row.
//
// Why ClosedXML and not EPPlus: EPPlus's license (v5+) requires a paid
// commercial license for exactly the kind of use this is - a for-profit
// comic shop's own POS software - which is the same category of risk
// already flagged for Comic Vine's API terms. ClosedXML is MIT-licensed,
// so there's nothing to track or revisit here. (See
// metron-integration-notes.md for the Comic Vine finding this mirrors.)
public static class LunarDistributionParser
{
    private static readonly string[] RequiredColumns = { "Title" };

    public static LunarParseResult Parse(string filePath)
    {
        var result = new LunarParseResult();

        if (!File.Exists(filePath))
        {
            result.Errors.Add($"File not found: {filePath}");
            return result;
        }

        using var workbook = new XLWorkbook(filePath);
        var ws = workbook.Worksheets.First();

        var headerRow = ws.Row(1);
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var header = cell.GetString().Trim();
            if (!string.IsNullOrWhiteSpace(header) && !columnIndex.ContainsKey(header))
                columnIndex[header] = cell.Address.ColumnNumber;
        }

        foreach (var required in RequiredColumns)
        {
            if (!columnIndex.ContainsKey(required))
            {
                result.Errors.Add($"Missing required column: {required}. Is this really a Lunar Distribution release file?");
                return result;
            }
        }

        // One real cover photo per data row, anchored somewhere on that
        // row - matched purely by ROW NUMBER, never by which column the
        // picture's own anchor cell happens to report, since there's
        // only ever one picture per row regardless of which column it's
        // technically anchored to (confirmed live: every picture in the
        // sample file anchored at the same column, one past the last
        // named column).
        var imagesByRow = new Dictionary<int, IXLPicture>();
        foreach (var pic in ws.Pictures)
        {
            var row = pic.TopLeftCell.Address.RowNumber;
            if (!imagesByRow.ContainsKey(row))
                imagesByRow[row] = pic;
        }

        string GetText(IXLRow xlRow, string column)
        {
            if (!columnIndex.TryGetValue(column, out var col)) return "";
            return xlRow.Cell(col).GetString().Trim();
        }

        decimal? GetDecimal(IXLRow xlRow, string column)
        {
            if (!columnIndex.TryGetValue(column, out var col)) return null;
            var cell = xlRow.Cell(col);
            if (cell.TryGetValue(out decimal d)) return d;
            return decimal.TryParse(cell.GetString().Trim(), out var d2) ? d2 : null;
        }

        DateOnly? GetDate(IXLRow xlRow, string column)
        {
            if (!columnIndex.TryGetValue(column, out var col)) return null;
            var cell = xlRow.Cell(col);
            if (cell.IsEmpty()) return null;
            // ClosedXML's typed DateTime accessor can still "succeed" on a
            // blank or stray non-date numeric cell, returning an
            // implausible date near Excel's own 1900 epoch instead of
            // failing outright (confirmed live 2026-11-26: 38 "Absolute
            // Universe" issues got FocDate 1900-01-01/1900-11-01 this
            // way, from a blank "FinalOrderCutoff" cell). No real
            // solicitation date in this app's whole history predates
            // 2021, so treat anything before year 2000 as a parse
            // failure instead of propagating it - same guard applies to
            // InstoreDate below since both columns share this helper.
            if (cell.TryGetValue(out DateTime dt) && dt.Year >= 2000) return DateOnly.FromDateTime(dt);
            return DateOnly.TryParse(cell.GetString().Trim(), out var d) ? d : null;
        }

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= lastRow; r++)
        {
            var xlRow = ws.Row(r);
            var title = GetText(xlRow, "Title");
            if (string.IsNullOrWhiteSpace(title)) continue; // blank spacer row

            var row = new LunarDistributionRow();
            LunarTitleParser.ApplyTitle(row, title);

            row.Publisher = GetText(xlRow, "Publisher").ToUpperInvariant();
            row.Description = CleanHtml(GetText(xlRow, "Description"));
            row.Price = GetDecimal(xlRow, "RetailCost");
            row.FocDate = GetDate(xlRow, "FinalOrderCutoff");
            row.ReleaseDate = GetDate(xlRow, "InstoreDate");

            var upc = GetText(xlRow, "UPC");
            var productCode = GetText(xlRow, "ProductCode");
            row.Upc = !string.IsNullOrWhiteSpace(upc) ? upc
                : !string.IsNullOrWhiteSpace(productCode) ? $"LUN-{productCode}"
                : null;

            if (imagesByRow.TryGetValue(r, out var pic))
            {
                try
                {
                    using var ms = new MemoryStream();
                    pic.ImageStream.Position = 0;
                    pic.ImageStream.CopyTo(ms);
                    row.ImageBytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Row {r} (\"{title}\"): couldn't read its embedded cover image: {ex.Message}");
                }
            }

            if (row.SeriesName == null)
            {
                // Not a single comic issue (no "#N" in the title) - note
                // it so a run's summary can explain why the imported
                // count doesn't match the file's total row count,
                // instead of that difference looking unexplained.
                result.Errors.Add($"Row {r}: \"{title}\" - no issue number found, skipped (likely a TP/HC/omnibus/non-comic item).");
                continue;
            }

            result.Rows.Add(row);
        }

        return result;
    }

    private static string? CleanHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        // Lunar's own Description column is a small HTML fragment
        // (<p> tags, &rsquo;/&mdash; entities) - the rest of this app
        // never stores markup in Cover.Description (LOCG/Metron/
        // Comicade all hand over plain text), so this strips tags and
        // decodes entities down to the same plain-text shape rather than
        // being the one source that renders literal HTML in the New
        // Books detail popup.
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }
}
