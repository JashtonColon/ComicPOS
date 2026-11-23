using ComicPos.Core;
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

        // Loaded once up front, not re-queried per row - same reasoning
        // as barcodesAddedThisRun just above: a series created by an
        // earlier row in THIS SAME import run needs to be visible to a
        // later row that references it too, and re-running
        // _db.AllSeries.AsEnumerable() on every row of a large CSV would
        // also just be a full-table reload each time for no reason.
        var seriesByKey = _db.AllSeries.ToList()
            .GroupBy(s => SeriesNameKey.Normalize(s.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in parsed.Rows)
        {
            if (barcodesAddedThisRun.ContainsKey(row.Barcode))
            {
                summary.Errors.Add($"Barcode {row.Barcode} appears more than once in this file - later row skipped.");
                continue;
            }

            // Same normalized-key matching as AddEntriesAsIssues/
            // InventoryService (see SeriesNameKey) - otherwise importing
            // a CSV with "The X" when "X" already exists from a weekly
            // scrape (or the reverse) creates a second, never-merging
            // Series row for what is really the same series.
            var seriesKey = SeriesNameKey.Normalize(row.SeriesName);
            if (!seriesByKey.TryGetValue(seriesKey, out var series))
            {
                series = new Series { Name = row.SeriesName, StartYear = row.StartYear };
                _db.AllSeries.Add(series);
                seriesByKey[seriesKey] = series;
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
                // New covers normally start at 0 stock (solicited but not
                // received yet). A ReceivedQty column in the CSV lets the
                // same import double as a "receive this shipment" action.
                var newCover = new Cover
                {
                    Barcode = row.Barcode,
                    CoverLetter = row.CoverLetter,
                    Price = row.Price,
                    StockQuantity = row.ReceivedQty
                };
                issue.Covers.Add(newCover);
                barcodesAddedThisRun[row.Barcode] = newCover;
                coversCreated++;
            }
            else
            {
                existingCover.Price = row.Price;
                existingCover.CoverLetter = row.CoverLetter;
                existingCover.StockQuantity += row.ReceivedQty;
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
