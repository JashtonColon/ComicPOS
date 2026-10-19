using ComicPos.Core.Services;

namespace ComicPos.App;

public class MainForm : Form
{
    public MainForm(IInventoryService inventory, ICheckoutService checkout)
    {
        Text = "Comic POS";
        Width = 1000;
        Height = 700;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var inventoryTab = new TabPage("Inventory");
        // your code: create an InventoryControl(inventory) and add it to inventoryTab.Controls
        InventoryControl inventoryControl = new InventoryControl(inventory);
        inventoryTab.Controls.Add(inventoryControl);

        var checkoutTab = new TabPage("Checkout");
        // your code: create a CheckoutControl(checkout) and add it to checkoutTab.Controls
        CheckoutControl checkoutControl = new CheckoutControl(checkout);
        checkoutTab.Controls.Add(checkoutControl);
        tabs.TabPages.Add(inventoryTab);
        tabs.TabPages.Add(checkoutTab);

        Controls.Add(tabs);

        tabs.SelectedIndexChanged += (s, e) =>
        {
            if (tabs.SelectedTab == inventoryTab)
                inventoryControl.ReloadData();
        };
    }
}