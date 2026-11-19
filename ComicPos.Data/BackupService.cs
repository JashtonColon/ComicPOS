using Microsoft.EntityFrameworkCore;
using ComicPos.Core.Services;

namespace ComicPos.Data;

public class BackupService : IBackupService
{
    private readonly ComicDbContext _db;

    public BackupService(ComicDbContext db)
    {
        _db = db;
    }

    public string BackupNow()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos", "Backups");
        Directory.CreateDirectory(folder);

        var fullPath = Path.Combine(folder, $"comic_pos_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db");

        // SQLite's VACUUM INTO writes a clean, consistent standalone copy of
        // the database in one step - correctly folds in anything still sitting
        // in the WAL journal, unlike a plain file copy of comic_pos.db which
        // can miss recent transactions that haven't been checkpointed yet.
        _db.Database.ExecuteSqlInterpolated($"VACUUM INTO {fullPath}");

        return fullPath;
    }
}
