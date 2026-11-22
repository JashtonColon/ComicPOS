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

    // Free account at metron.cloud - a cover-art source (see
    // MetronService) tried during Re-scan Existing for every issue still
    // missing a cover, ahead of GCD (GCD runs as the fallback when Metron
    // misses or is rate-limited - see AutoFetchService.RescanExistingBooks
    // for why that order was flipped). Unlike Comic Vine and GCD, Metron
    // has no anonymous tier at all - without this, Metron is simply
    // skipped rather than attempted anonymously.
    //
    // Metron's API used to accept your plain account username/password
    // (HTTP Basic Auth), but that was removed - it now requires a
    // per-account API token (sent as a Bearer token) instead, generated
    // from your metron.cloud account.
    public string? MetronApiToken { get; set; }

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

    // One-time data repair, applied automatically on the first startup
    // after this fix shipped (see Program.cs) - never surfaced in
    // Settings, never meant to be toggled by hand. Before this fix,
    // GCD/Metron cover lookups used a series-wide StartYear (often wrong
    // for a character relaunched many times, e.g. Wonder Woman's series
    // record was linked to the original 1942 Comic Vine volume) instead
    // of each issue's own release date, so a store could end up with the
    // wrong era's cover saved for issues that already looked "filled in"
    // (CoverImagePath was set, just to the wrong image) - something a
    // normal Re-scan never revisits, since it only fills in blanks. This
    // flag makes sure that one-time cleanup (clearing those wrong covers
    // so Re-scan can correctly refill them) runs exactly once, not on
    // every startup.
    public bool WrongEraCoverCleanupApplied { get; set; }
}
