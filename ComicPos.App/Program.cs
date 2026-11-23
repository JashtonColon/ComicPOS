using Microsoft.EntityFrameworkCore;
using ComicPos.Data;

namespace ComicPos.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Catch anything unhandled and show it in a dialog instead of letting
        // the app vanish with no explanation - a shop running this day to
        // day needs to see what happened, not just a crash.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
            MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}", "Comic POS - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            MessageBox.Show($"An unexpected error occurred:\n\n{(e.ExceptionObject as Exception)?.Message}", "Comic POS - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        using var db = new ComicDbContext();

        // Applies any pending EF Core migrations automatically, creating the
        // database from scratch on first run. A shop installing this fresh
        // should never have to know what "dotnet ef database update" means.
        try
        {
            db.Database.Migrate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Comic POS could not set up its database and cannot start:\n\n{ex.Message}",
                "Comic POS - Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var inventory = new InventoryService(db);
        var customers = new CustomerService(db);
        // customers constructed above checkout now - CheckoutService
        // needs it (store credit redemption/refund, see
        // ICheckoutService.ApplyStoreCredit) and didn't exist yet when
        // this line was still `new CheckoutService(db, inventory)`.
        var checkout = new CheckoutService(db, inventory, customers);
        var history = new SalesHistoryService(db);
        var pullLists = new PullListService(db);
        var issueHolds = new IssueHoldService(db);
        var import = new ImportService(db);
        var focReport = new FocReportService(db);
        var email = new EmailService();
        var emailSettings = new EmailSettingsService();
        var dashboard = new DashboardService(db);
        var storeSettings = new StoreSettingsService();
        var backupService = new BackupService(db);
        var comicVine = new ComicVineService(storeSettings);
        var comicList = new ComicListService();
        var comicadeFoc = new ComicadeFocService();
        var gcd = new GcdService();
        var metron = new MetronService(storeSettings);
        // locg wasn't being constructed here at all - AutoFetchService's
        // constructor already needed it (added when LOCG became a
        // discovery source) and this call was out of sync with it,
        // meaning ComicPos.App hasn't actually compiled since then.
        // Still no WinForms UI for Metron discovery or store credit
        // (browser mode only gets that) - this is purely keeping the
        // shared backend wiring here compiling.
        var locg = new LocgService();
        var autoFetch = new AutoFetchService(db, comicVine, comicList, comicadeFoc, gcd, metron, locg, storeSettings);

        Application.Run(new MainForm(inventory, checkout, history, customers, pullLists, issueHolds, import, focReport, email, emailSettings, dashboard, storeSettings, backupService, autoFetch));
    }
}
