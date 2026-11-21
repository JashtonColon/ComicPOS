namespace ComicPos.Core.Models;

public class StoreSettings
{
    public string StoreName { get; set; } = "Comic POS";
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public decimal TaxRatePercent { get; set; } = 0m;

    // Full path to a logo image file on this machine (PNG/JPG), shown on
    // printed/on-screen receipts. Null/empty means "no logo".
    public string? LogoPath { get; set; }

    // Free API key from comicvine.gamespot.com/api/, used to look up new
    // issues for series the store carries. Null/empty disables that feature.
    public string? ComicVineApiKey { get; set; }

    // When the weekly new-releases check last ran, so the app knows whether
    // one is due (on startup, and roughly every Sunday while it's open).
    public DateTime? LastAutoFetchRun { get; set; }

    // Comma-separated publisher names (e.g. "Marvel, DC Comics, Image Comics")
    // used to filter the weekly "new #1s" discovery pass, which otherwise
    // searches EVERY publisher on Comic Vine (including manga/small press)
    // for brand-new series launches. Blank/null = no filter, include all.
    public string? TrackedPublishers { get; set; }

    // Free account at metron.cloud - a third, independent cover-art
    // source (see MetronService) tried during Re-scan Existing when
    // Comic Vine and GCD both come up empty for an issue. Unlike Comic
    // Vine and GCD, Metron has no anonymous tier at all - without these,
    // Metron is simply skipped rather than attempted anonymously.
    public string? MetronUsername { get; set; }
    public string? MetronPassword { get; set; }

    // On by default, unlike the FOC auto-email opt-in - a background
    // database backup has no outside-facing side effect to ask permission
    // for first (nothing sent anywhere, no email, no cost), so the safer
    // default is backups existing without anyone having to think to turn
    // it on. See MainForm's hourly tick / BackupService.
    public bool AutoBackupEnabled { get; set; } = true;

    // When the app last backed up the database on its own - separate from
    // a person clicking "Backup Database Now" in Settings, which always
    // runs regardless of this timestamp.
    public DateTime? LastAutoBackupAt { get; set; }
}
