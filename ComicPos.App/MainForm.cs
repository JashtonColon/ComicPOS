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
        IDashboardService dashboard)
    {
        Text = "Comic POS";
        Width = 1000;
        Height = 700;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var inventoryTab = new TabPage("Inventory");
        InventoryControl inventoryControl = new InventoryControl(inventory);
        inventoryTab.Controls.Add(inventoryControl);

        var checkoutTab = new TabPage("Checkout");
        CheckoutControl checkoutControl = new CheckoutControl(checkout, customers);
        checkoutTab.Controls.Add(checkoutControl);

        var historyTab = new TabPage("History");
        HistoryControl historyControl = new HistoryControl(history);
        historyTab.Controls.Add(historyControl);

        var customerTab = new TabPage("Customers");
        CustomerControl customerControl = new CustomerControl(customers, history, pullLists, inventory);
        customerTab.Controls.Add(customerControl);

        var importTab = new TabPage("Import");
        ImportControl importControl = new ImportControl(import);
        importTab.Controls.Add(importControl);

        var focTab = new TabPage("FOC Report");
        FocReportControl focReportControl = new FocReportControl(focReport, email, emailSettings);
        focTab.Controls.Add(focReportControl);

        var settingsTab = new TabPage("Settings");
        SettingsControl settingsControl = new SettingsControl(emailSettings);
        settingsTab.Controls.Add(settingsControl);

        var dashboardTab = new TabPage("Dashboard");
        DashboardControl dashboardControl = new DashboardControl(dashboard);
        dashboardTab.Controls.Add(dashboardControl);

        tabs.TabPages.Add(dashboardTab);
        tabs.TabPages.Add(inventoryTab);
        tabs.TabPages.Add(checkoutTab);
        tabs.TabPages.Add(historyTab);
        tabs.TabPages.Add(customerTab);
        tabs.TabPages.Add(importTab);
        tabs.TabPages.Add(focTab);
        tabs.TabPages.Add(settingsTab);

        Controls.Add(tabs);

        tabs.SelectedIndexChanged += (s, e) =>
        {
            if (tabs.SelectedTab == inventoryTab)
                inventoryControl.ReloadData();
            else if (tabs.SelectedTab == dashboardTab)
                dashboardControl.RefreshAll();
        };
    }
}
