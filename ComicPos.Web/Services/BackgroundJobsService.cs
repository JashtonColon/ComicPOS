using ComicPos.Core.Services;

namespace ComicPos.Web.Services;

// Web equivalent of MainForm's hourly maintenance timer: the weekly New
// Books check, the daily FOC-report auto-email, and the daily auto-backup.
//
// Runs on its own background timer with its own DI scope (and therefore
// its own DbContext instance) per tick - deliberately NOT reusing a
// browser circuit's scoped services the way a page does. A Blazor Server
// circuit's scope lives for as long as that browser tab stays connected,
// so if this ran inside a page's scope instead, a slow Comic Vine check
// kicked off from one open tab would hold that same tab's DbContext while
// the person tried to click around elsewhere in it - EF Core's DbContext
// isn't safe for concurrent use the same instance, which is exactly the
// freeze the desktop app's Task.Run/form-disable wrapper existed to avoid.
// A dedicated scope per tick sidesteps that entirely instead of trying to
// replicate the desktop's "disable the whole form" approach for a web app
// where "the whole form" would have to mean every open tab at once.
public class BackgroundJobsService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BackgroundJobsService> _logger;

    public BackgroundJobsService(IServiceProvider services, ILogger<BackgroundJobsService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Also try once at startup, in case the app is only ever kept
        // running briefly and the hourly tick would rarely get a chance
        // to fire - same reasoning as MainForm's own startup call.
        await RunOnceAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync();
        }
    }

    private async Task RunOnceAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var autoFetch = scope.ServiceProvider.GetRequiredService<IAutoFetchService>();
            var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var emailSettings = scope.ServiceProvider.GetRequiredService<IEmailSettingsService>();
            var focReport = scope.ServiceProvider.GetRequiredService<IFocReportService>();
            var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
            var storeSettings = scope.ServiceProvider.GetRequiredService<IStoreSettingsService>();

            // CheckForNewIssues can mean dozens of sequential Comic Vine
            // HTTP calls - fine to block this background tick's own thread
            // for a while, since nothing else is waiting on it the way the
            // desktop app's UI thread was.
            await Task.Run(() => RunWeeklyCheckIfDue(autoFetch));
            TrySendAutoFocReport(email, emailSettings, focReport);
            TryAutoBackup(backupService, storeSettings);
        }
        catch (Exception ex)
        {
            // Never let one bad tick (DB momentarily locked, disk full,
            // Comic Vine unreachable, etc.) take down the whole background
            // loop - log it and just try again next hour.
            _logger.LogError(ex, "Comic POS background maintenance tick failed");
        }
    }

    private void RunWeeklyCheckIfDue(IAutoFetchService autoFetch)
    {
        if (!autoFetch.IsWeeklyCheckDue()) return;
        try
        {
            autoFetch.CheckForNewIssues();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Weekly New Books check failed");
        }
    }

    // Opt-in (Settings > Email > "Auto-email FOC report daily"). Sends the
    // same compact buy report the FOC Report page's "Send Email to Admin"
    // button sends, once a day, with no manual click needed. Skipped
    // entirely if there's nothing to buy, to avoid pointless daily noise;
    // skipped (and retried next hour) if sending fails, e.g. bad SMTP
    // settings or no network, so one bad night doesn't disable it for good.
    private void TrySendAutoFocReport(IEmailService email, IEmailSettingsService emailSettings, IFocReportService focReport)
    {
        var settings = emailSettings.Load();
        if (!settings.AutoSendFocReport) return;
        if (settings.LastFocReportSentAt.HasValue &&
            (DateTime.Now - settings.LastFocReportSentAt.Value).TotalHours < 20)
            return;

        try
        {
            var daysAhead = settings.FocReportDaysAhead > 0 ? settings.FocReportDaysAhead : 14;
            var entries = focReport.GetUpcomingFocReport(daysAhead);
            if (entries.Count > 0)
                email.SendFocReport(entries, settings);

            settings.LastFocReportSentAt = DateTime.Now;
            emailSettings.Save(settings);
        }
        catch (Exception ex)
        {
            // Leave LastFocReportSentAt untouched so the next tick retries.
            _logger.LogWarning(ex, "Auto FOC report email failed");
        }
    }

    // Opt-out (Settings > Backups > "Automatically back up daily"). Mirrors
    // TrySendAutoFocReport's "is it time yet?" shape exactly, just against
    // BackupService/LastAutoBackupAt instead of the email side.
    private void TryAutoBackup(IBackupService backupService, IStoreSettingsService storeSettings)
    {
        var settings = storeSettings.Load();
        if (!settings.AutoBackupEnabled) return;
        if (settings.LastAutoBackupAt.HasValue &&
            (DateTime.Now - settings.LastAutoBackupAt.Value).TotalHours < 20)
            return;

        try
        {
            backupService.BackupNow();
            settings.LastAutoBackupAt = DateTime.Now;
            storeSettings.Save(settings);
        }
        catch (Exception ex)
        {
            // Leave LastAutoBackupAt untouched so the next tick retries -
            // e.g. the disk was briefly full or the backups folder was
            // locked by another process.
            _logger.LogWarning(ex, "Auto backup failed");
        }
    }
}
