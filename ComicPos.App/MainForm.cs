using ComicPos.Core.Services;

namespace ComicPos.App;

public class MainForm : Form
{
    public MainForm(IInventoryService inventory, ICheckoutService checkout, ISalesHistoryService history, ICustomerService customers)
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
        CheckoutControl checkoutControl = new CheckoutControl(checkout);
        checkoutTab.Controls.Add(checkoutControl);

        var historyTab = new TabPage("History");
        HistoryControl historyControl = new HistoryControl(history);
        historyTab.Controls.Add(historyControl);

        var customerTab = new TabPage("Customers");
        CustomerControl customerControl = new CustomerControl(customers);
        customerTab.Controls.Add(customerControl);

        tabs.TabPages.Add(inventoryTab);
        tabs.TabPages.Add(checkoutTab);
        tabs.TabPages.Add(historyTab);
        tabs.TabPages.Add(customerTab);

        Controls.Add(tabs);

        tabs.SelectedIndexChanged += (s, e) =>
        {
            if (tabs.SelectedTab == inventoryTab)
                inventoryControl.ReloadData();
        };
    }
}       