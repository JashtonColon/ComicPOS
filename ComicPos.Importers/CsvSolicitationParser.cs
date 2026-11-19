using System.Text;

namespace ComicPos.Importers;

public class ParseResult
{
    public List<SolicitationRow> Rows { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

// Expected CSV header (any order, case-insensitive):
// Series,StartYear,IssueNumber,Publisher,CoverLetter,Barcode,Price,FocDate,ReleaseDate,Type
//
// Only Series, IssueNumber, and Barcode are required - everything else
// falls back to a sensible default if missing or blank.
public static class CsvSolicitationParser
{
    public static ParseResult Parse(string filePath)
    {
        var result = new ParseResult();

        if (!File.Exists(filePath))
        {
            result.Errors.Add($"File not found: {filePath}");
            return result;
        }

        var lines = File.ReadAllLines(filePath);
        if (lines.Length == 0)
        {
            result.Errors.Add("File is empty.");
            return result;
        }

        var headers = SplitCsvLine(lines[0]).Select(h => h.Trim()).ToArray();
        var headerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
            headerIndex[headers[i]] = i;

        foreach (var required in new[] { "Series", "IssueNumber", "Barcode" })
        {
            if (!headerIndex.ContainsKey(required))
            {
                result.Errors.Add($"Missing required column: {required}");
                return result;
            }
        }

        for (int lineNum = 1; lineNum < lines.Length; lineNum++)
        {
            var line = lines[lineNum];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = SplitCsvLine(line);

            string Get(string column)
            {
                if (!headerIndex.TryGetValue(column, out var idx)) return "";
                return idx < fields.Length ? fields[idx].Trim() : "";
            }

            var seriesName = Get("Series");
            var barcode = Get("Barcode");
            var issueNumberText = Get("IssueNumber");

            if (string.IsNullOrWhiteSpace(seriesName) || string.IsNullOrWhiteSpace(barcode) || string.IsNullOrWhiteSpace(issueNumberText))
            {
                result.Errors.Add($"Line {lineNum + 1}: missing Series, Barcode, or IssueNumber - skipped.");
                continue;
            }

            if (!float.TryParse(issueNumberText, out var issueNumber))
            {
                result.Errors.Add($"Line {lineNum + 1}: '{issueNumberText}' is not a valid issue number - skipped.");
                continue;
            }

            int.TryParse(Get("StartYear"), out var startYear);
            decimal.TryParse(Get("Price"), out var price);

            DateOnly? focDate = DateOnly.TryParse(Get("FocDate"), out var focParsed) ? focParsed : null;
            DateOnly? releaseDate = DateOnly.TryParse(Get("ReleaseDate"), out var releaseParsed) ? releaseParsed : null;

            var coverLetter = Get("CoverLetter");
            var type = Get("Type");
            int.TryParse(Get("ReceivedQty"), out var receivedQty);

            result.Rows.Add(new SolicitationRow
            {
                SeriesName = seriesName,
                StartYear = startYear,
                IssueNumber = issueNumber,
                Publisher = Get("Publisher").ToUpper(),
                CoverLetter = string.IsNullOrWhiteSpace(coverLetter) ? "A" : coverLetter.ToUpper(),
                Barcode = barcode,
                Price = price,
                FocDate = focDate,
                ReleaseDate = releaseDate,
                Type = string.IsNullOrWhiteSpace(type) ? "SingleIssue" : type,
                ReceivedQty = receivedQty
            });
        }

        return result;
    }

    // Minimal CSV field splitter: handles double-quoted fields that may
    // contain commas, and "" as an escaped quote inside a quoted field.
    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
