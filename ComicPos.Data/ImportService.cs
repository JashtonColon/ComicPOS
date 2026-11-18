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

        foreach (var row in parsed.Rows)
        {
            var series = _db.AllSeries.FirstOrDefault(s => s.Name == row.SeriesName);
            if (series == null)
            {
                series = new Series { Name = row.SeriesName, StartYear = row.StartYear };
                _db.AllSeries.Add(series);
                summary.SeriesCreated++;
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
                summary.IssuesCreated++;
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
                issue.Covers.Add(new Cover
                {
                    Barcode = row.Barcode,
                    CoverLetter = row.CoverLetter,
                    Price = row.Price,
                    StockQuantity = 0
                });
                summary.CoversCreated++;
            }
            else
            {
                existingCover.Price = row.Price;
                existingCover.CoverLetter = row.CoverLetter;
                summary.CoversUpdated++;
            }
        }

        _db.SaveChanges();
        return summary;
    }
}
