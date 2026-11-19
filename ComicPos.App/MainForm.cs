using ComicPos.Core.Services;

namespace ComicPos.App;

public class MainForm : Form
{
    public MainForm(
        IInventoryService inventory,
        ICheckoutService checkout,
        ISalesHistoryService history,
        ICustomerService customers,
        IPullListService pullLists,
        IImportService import,
        IFocReportService focReport,
        IEmailService email,
        IEmailSettingsService emailSettings,
        IDashboardService dashboard,
        IStoreSettingsService storeSettings,
        IBackupService backupService,
        IAutoFetchService autoFetch)
    {
        Text = "Comic POS";
        Width = 1050;
        Height = 720;
        MinimumSize = new Size(900, 600);
        UiTheme.StyleForm(this);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = UiTheme.BoldFont,
            Padding = new Point(16, 6)
        };

        var inventoryTab = new TabPage("Inventory");
        InventoryControl inventoryControl = new InventoryControl(inventory);
        inventoryTab.Controls.Add(inventoryControl);

        var checkoutTab = new TabPage("Checkout");
        CheckoutControl checkoutControl = new CheckoutControl(checkout, customers, storeSettings);
        checkoutTab.Controls.Add(checkoutControl);

        var historyTab = new TabPage("History");
        HistoryControl historyControl = new HistoryControl(history, checkout);
        historyTab.Controls.Add(historyControl);

        var customerTab = new TabPage("Customers");
        CustomerControl customerControl = new CustomerControl(customers, history, pullLists, inventory, checkout);
        customerTab.Controls.Add(customerControl);

        var importTab = new TabPage("Import");
        ImportControl importControl = new ImportControl(import);
        importTab.Controls.Add(importControl);

        var focTab = new TabPage("FOC Report");
        FocReportControl focReportControl = new FocReportControl(focReport, email, emailSettings);
        focTab.Controls.Add(focReportControl);

        var settingsTab = new TabPage("Settings");
        SettingsControl settingsControl = new SettingsControl(emailSettings, storeSettings, backupService);
        settingsTab.Controls.Add(settingsControl);

        var newBooksTab = new TabPage("New Books");
        AutoFetchControl autoFetchControl = new AutoFetchControl(autoFetch);
        newBooksTab.Controls.Add(autoFetchControl);

        var dashboardTab = new TabPage("Dashboard");
        DashboardControl dashboardControl = new DashboardControl(dashboard);
        dashboardTab.Controls.Add(dashboardControl);

        tabs.TabPages.Add(dashboardTab);
        tabs.TabPages.Add(inventoryTab);
        tabs.TabPages.Add(checkoutTab);
        tabs.TabPages.Add(historyTab);
        tabs.TabPages.Add(customerTab);
        tabs.TabPages.Add(importTab);
        tabs.TabPages.Add(newBooksTab);
        tabs.TabPages.Add(focTab);
        tabs.TabPages.Add(settingsTab);

        Controls.Add(tabs);

        tabs.SelectedIndexChanged += (s, e) =>
        {
            if (tabs.SelectedTab == inventoryTab)
                inventoryControl.ReloadData();
            else if (tabs.SelectedTab == dashboardTab)
                dashboardControl.RefreshAll();
            else if (tabs.SelectedTab == newBooksTab)
                autoFetchControl.RefreshAll();
        };

        // New Books checks itself on startup if a week has passed (see
        // AutoFetchControl's constructor). This timer covers the case where
        // the app is just left open across a Sunday: about once an hour,
        // if it's Sunday and a week has genuinely passed, it runs again on
        // its own. It also carries the daily FOC-report auto-email check
        // (see below) since both are lightweight "is it time yet?" checks
        // that fit naturally on the same hourly tick.
        //
        // IMPORTANT: the weekly comics check always goes through
        // autoFetchControl.RunWeeklyCheckIfDue() - never call
        // autoFetch.CheckForNewIssues() directly from here. That method can
        // make dozens of blocking HTTP calls and used to freeze the whole
        // app when run straight on the UI thread; RunWeeklyCheckIfDue()
        // wraps it in the same background-thread/form-disable safety the
        // "Check Now" button uses.
        var hourlyMaintenanceTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
        hourlyMaintenanceTimer.Tick += (s, e) =>
        {
            // If a comics check is already running in the background (its
            // Form.Enabled = false while it works), skip this tick entirely
            // rather than touching the shared ComicDbContext concurrently -
            // both the weekly check below and the FOC auto-send query it.
            if (!autoFetchControl.IsCheckRunning)
            {
                autoFetchControl.RunWeeklyCheckIfDue();
                TrySendAutoFocReport();
            }
        };
        hourlyMaintenanceTimer.Start();

        // Also try once at startup, in case the app is only ever opened
        // briefly and the hourly tick would rarely get a chance to fire.
        if (!autoFetchControl.IsCheckRunning)
            TrySendAutoFocReport();

        // Opt-in (see Settings > Email > "Auto-email FOC report daily").
        // Sends the same compact buy report the "Send Email to Admin"
        // button on the FOC Report tab sends, once a day, with no manual
        // click needed - matching the original brief's "send an email to
        // the admin... before FOC" requirement. Skipped entirely if there's
        // nothing to buy, to avoid pointless daily noise; skipped (and
        // retried next hour) if sending fails, e.g. bad SMTP settings or no
        // network, so one bad night doesn't disable it permanently.
        void TrySendAutoFocReport()
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
            catch
            {
                // Leave LastFocReportSentAt untouched so the next hourly
                // tick retries. Nothing else to do here - there's no
                // status bar or log on this form to surface the failure
                // to, and a scheduled background email is not worth a
                // pop-up dialog interrupting whatever the person is doing.
            }
        }
    }
}
