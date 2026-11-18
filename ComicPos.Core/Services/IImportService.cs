namespace ComicPos.Core.Services;

public class ImportSummary
{
    public int SeriesCreated { get; set; }
    public int IssuesCreated { get; set; }
    public int CoversCreated { get; set; }
    public int CoversUpdated { get; set; }
    public List<string> Errors { get; set; } = new();
}

public interface IImportService
{
    ImportSummary ImportFromCsv(string filePath);
}
