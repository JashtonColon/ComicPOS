using ComicPos.Core.Models;
using ComicPos.Core.Services;
using ComicPos.Importers;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class ImportService : IImportService
{
    private readonly ComicDbContext _db;

    public ImportService(ComicDbContext db)
    {
        _db = db;
    }

    public ImportSummary ImportFromCsv(string filePath)
    {
        var summary = new ImportSummary();
        var parsed = CsvSolicitationParser.Parse(filePath);
        summary.Errors.AddRange(parsed.Errors);

        int seriesCreated = 0, issuesCreated = 0, coversCreated = 0, coversUpdated = 0;

        // Tracks barcodes added earlier in THIS SAME import run, since a cover
        // added here isn't visible to a _db.Covers query until SaveChanges runs.
        // Without this, two rows in one file sharing a barcode would both get
        // added as new Covers and the unique-barcode index would throw when
        // SaveChanges finally ran, aborting the entire import with nothing saved.
        var barcodesAddedThisRun = new Dictionary<string, Cover>();

        foreach (var row in parsed.Rows)
        {
            if (barcodesAddedThisRun.ContainsKey(row.Barcode))
            {
                summary.Errors.Add($"Barcode {row.Barcode} appears more than once in this file - later row skipped.");
                continue;
            }

            var series = _db.AllSeries.FirstOrDefault(s => s.Name == row.SeriesName);
            if (series == null)
            {
                series = new Series { Name = row.SeriesName, StartYear = row.StartYear };
                _db.AllSeries.Add(series);
                seriesCreated++;
            }

            var issue = _db.Issues
                .Include(i => i.Covers)
                .FirstOrDefault(i => i.Series == series && i.IssueNumber == row.IssueNumber);

            if (issue == null)
            {
                issue = new Issue
                {
                    Series = series,
                    IssueNumber = row.IssueNumber,
                    Publisher = row.Publisher,
                    FocDate = row.FocDate,
                    ReleaseDate = row.ReleaseDate,
                    Type = row.Type == "CollectedEdition" ? IssueType.CollectedEdition : IssueType.SingleIssue
                };
                _db.Issues.Add(issue);
                issuesCreated++;
            }
            else
            {
                // Re-importing an already-known issue still refreshes publisher/dates,
                // since FOC and release dates are often announced or revised later.
                issue.Publisher = row.Publisher;
                issue.FocDate = row.FocDate;
                issue.ReleaseDate = row.ReleaseDate;
            }

            var existingCover = _db.Covers.FirstOrDefault(c => c.Barcode == row.Barcode);
            if (existingCover == null)
            {
                // New covers start at 0 stock - they haven't actually arrived yet,
                // stock gets added when the shipment is received.
                var newCover = new Cover
                {
                    Barcode = row.Barcode,
                    CoverLetter = row.CoverLetter,
                    Price = row.Price,
                    StockQuantity = 0
                };
                issue.Covers.Add(newCover);
                barcodesAddedThisRun[row.Barcode] = newCover;
                coversCreated++;
            }
            else
            {
                existingCover.Price = row.Price;
                existingCover.CoverLetter = row.CoverLetter;
                coversUpdated++;
            }
        }

        try
        {
            _db.SaveChanges();
        }
        catch (Exception ex)
        {
            // SaveChanges is one implicit transaction - if it throws, nothing from
            // this run was persisted, so report zero counts rather than the
            // in-memory tallies above (which would otherwise overstate what happened).
            summary.Errors.Add($"Import failed, no changes were saved: {ex.Message}");
            return summary;
        }

        summary.SeriesCreated = seriesCreated;
        summary.IssuesCreated = issuesCreated;
        summary.CoversCreated = coversCreated;
        summary.CoversUpdated = coversUpdated;
        return summary;
    }
}
