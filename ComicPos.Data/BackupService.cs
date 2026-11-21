using System.Linq;
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

    // Now that backups can also happen automatically once a day (see
    // MainForm/StoreSettings.AutoBackupEnabled), nothing deletes old ones
    // on its own - left unchecked, this folder grows by one full database
    // copy a day forever. Capped at the most recent N so disk usage stays
    // bounded without anyone having to remember to clean it out, while
    // still keeping a long enough history to recover from a mistake
    // noticed days (not just hours) later.
    private const int MaxBackupsToKeep = 30;

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

        PruneOldBackups(folder);

        return fullPath;
    }

    private static void PruneOldBackups(string folder)
    {
        try
        {
            var oldBackups = new DirectoryInfo(folder)
                .GetFiles("comic_pos_backup_*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(MaxBackupsToKeep);

            foreach (var file in oldBackups)
                file.Delete();
        }
        catch
        {
            // Pruning is a nice-to-have, not the point of this method - a
            // locked or permission-denied old file should never make the
            // backup that was just written successfully look like it failed.
        }
    }
}
