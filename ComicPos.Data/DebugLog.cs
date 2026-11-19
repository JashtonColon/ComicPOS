namespace ComicPos.Data;

// A plain-text trace of exactly what the last "Check Now" run did, written
// to checknow-debug.txt at the repo root (next to ComicPos.slnx) - not
// %LOCALAPPDATA%, specifically so it's easy to just open and read, or
// paste from, without digging through AppData. Overwritten on every run
// (it's "what just happened", not a growing history). Listed in
// .gitignore so it never gets committed.
public static class DebugLog
{
    private const string FileName = "checknow-debug.txt";

    private static string ResolvePath()
    {
        // Walk up from wherever the app is actually running (bin/Debug/
        // net10.0/...) looking for the folder that has ComicPos.slnx in
        // it - that's the repo root. Falls back to writing next to the
        // exe if that's ever not found (e.g. a published/installed
        // build), so this never crashes a real run over a missing file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ComicPos.slnx")))
                return Path.Combine(dir.FullName, FileName);
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, FileName);
    }

    // Overwrites the file with this run's full trace. Swallows its own
    // failures (e.g. the file is open/locked elsewhere) - a debug log
    // that can't be written is never a reason to fail the actual check.
    public static void WriteCheckNowLog(string content)
    {
        try
        {
            File.WriteAllText(ResolvePath(), content);
        }
        catch
        {
            // Best-effort only.
        }
    }
}
