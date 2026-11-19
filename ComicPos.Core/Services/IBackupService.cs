namespace ComicPos.Core.Services;

public interface IBackupService
{
    // Returns the full path of the backup file it just wrote.
    string BackupNow();
}
