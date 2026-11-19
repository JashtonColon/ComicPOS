using System.Text;
using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class CheckoutControl : UserControl
{
    private readonly ICheckoutService _checkout;
    private readonly ICustomerService _customers;
    private readonly IStoreSettingsService _storeSettings;
    private Sale _sale;
    private readonly TextBox txtBarcode = new();
    private readonly DataGridView gridCart = new();
    private readonly Button btnRemove = new();

    private readonly TextBox txtCustomerSearch = new();
    private readonly ListBox lstCustomerResults = new();
    private readonly Label lblCustomer = new();
    private List<Customer> _customerResults = new();
    private readonly Panel customerPanel = new();

    // Totals / payment panel
    private readonly Panel bottomPanel;
    private readonly Label lblSubtotal = new();
    private readonly Label lblTax = new();
    private readonly Label lblGrandTotal = new();
    private readonly TextBox txtCashTendered = new();
    private readonly Label lblChange = new();
    private readonly Button btnComplete = new();

    public CheckoutControl(ICheckoutService checkout, ICustomerService customers, IStoreSettingsService storeSettings)
    {
        _checkout = checkout;
        _customers = customers;
        _storeSettings = storeSettings;
        _sale = _checkout.StartSale();

        Dock = DockStyle.Fill;

        customerPanel.Dock = DockStyle.Top;
        customerPanel.Height = lstCustomerResults.Visible ? 120 : 60;

        lblCustomer.Dock = DockStyle.Top;
        lblCustomer.Height = 32;
        lblCustomer.Text = "Customer: Walk-in";
        lblCustomer.Padding = new Padding(0, 6, 0, 6);

        txtCustomerSearch.Dock = DockStyle.Top;
        txtCustomerSearch.PlaceholderText = "Search customer (optional)...";
        txtCustomerSearch.TextChanged += (s, e) => SearchCustomers();

        lstCustomerResults.Dock = DockStyle.Top;
        customerPanel.Height = lstCustomerResults.Visible ? 120 : 60;
        lstCustomerResults.SelectedIndexChanged += CustomerResult_Selected;

        customerPanel.Controls.Add(lstCustomerResults);
        customerPanel.Controls.Add(txtCustomerSearch);
        customerPanel.Controls.Add(lblCustomer);

        txtBarcode.Dock = DockStyle.Top;
        txtBarcode.PlaceholderText = "Scan barcode...";

        btnRemove.Dock = DockStyle.Top;
        btnRemove.Height = 30;
        btnRemove.Text = "Remove Selected Item";
        btnRemove.Click += BtnRemove_Click;

        ConfigureGridColumns();
        gridCart.Dock = DockStyle.Fill;
        gridCart.AllowUserToAddRows = false;
        gridCart.RowHeadersVisible = false;
        gridCart.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        gridCart.CellEndEdit += GridCart_CellEndEdit;
        gridCart.KeyDown += GridCart_KeyDown;

        // --- totals / payment panel (Bottom), built with a TableLayoutPanel
        // so adding rows never runs into WinForms' reverse-dock-order gotcha ---
        bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 220 };
        var totalsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
        totalsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        totalsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        void AddRow(Control left, Control right, int height)
        {
            left.Dock = DockStyle.Fill;
            right.Dock = DockStyle.Fill;
            totalsLayout.Controls.Add(left, 0, totalsLayout.RowStyles.Count);
            totalsLayout.Controls.Add(right, 1, totalsLayout.RowStyles.Count);
            totalsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        }

        lblSubtotal.TextAlign = ContentAlignment.MiddleLeft;
        lblTax.TextAlign = ContentAlignment.MiddleLeft;
        lblGrandTotal.TextAlign = ContentAlignment.MiddleLeft;
        lblGrandTotal.Font = new Font(lblGrandTotal.Font, FontStyle.Bold);
        lblChange.TextAlign = ContentAlignment.MiddleLeft;

        AddRow(new Label { Text = "Subtotal:" }, lblSubtotal, 26);
        AddRow(new Label { Text = "Tax:" }, lblTax, 26);
        AddRow(new Label { Text = "Total:" }, lblGrandTotal, 30);
        AddRow(new Label { Text = "Cash Tendered:" }, txtCashTendered, 30);
        AddRow(new Label { Text = "Change Due:" }, lblChange, 26);

        btnComplete.Text = "Complete Sale";
        totalsLayout.Controls.Add(btnComplete, 0, totalsLayout.RowStyles.Count);
        totalsLayout.SetColumnSpan(btnComplete, 2);
        totalsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        btnComplete.Dock = DockStyle.Fill;

        bottomPanel.Controls.Add(totalsLayout);

        txtCashTendered.TextChanged += (s, e) => UpdateChangeDue();

        Controls.Add(gridCart);
        Controls.Add(bottomPanel);
        Controls.Add(btnRemove);
        Controls.Add(txtBarcode);
        Controls.Add(customerPanel);

        txtBarcode.KeyDown += TxtBarcode_KeyDown;
        btnComplete.Click += BtnComplete_Click;
        btnComplete.Tag = "primary";

        RefreshCart();

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
    }

    // Explicit columns, instead of letting the grid guess them from an
    // anonymous type. This is what lets us mark some ReadOnly and others
    // editable, which auto-generated columns don't let us control per-column.
    private void ConfigureGridColumns()
    {
        gridCart.AutoGenerateColumns = false;
        gridCart.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series", ReadOnly = true });
        gridCart.Columns.Add(new DataGridViewTextBoxColumn { Name = "Issue", HeaderText = "Issue", ReadOnly = true });
        gridCart.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cover", HeaderText = "Cover", ReadOnly = true });
        gridCart.Columns.Add(new DataGridViewTextBoxColumn { Name = "Quantity", HeaderText = "Quantity", ReadOnly = false });
        gridCart.Columns.Add(new DataGridViewTextBoxColumn { Name = "PriceEach", HeaderText = "Price Each", ReadOnly = false });
        gridCart.Columns.Add(new DataGridViewTextBoxColumn { Name = "Discount", HeaderText = "Discount", ReadOnly = false });
        gridCart.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "Total", ReadOnly = true });
    }

    private void TxtBarcode_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;

        var barcode = txtBarcode.Text;
        var success = _checkout.ScanItem(_sale, barcode); // quantity always 1 at scan time now; edit it in the grid after
        if (!success)
        {
            MessageBox.Show($"Item with barcode {barcode} not found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            RefreshCart();
        }
        txtBarcode.Clear();
        txtBarcode.Focus();
    }

    // Fires whenever the user finishes editing a cell (clicks away or hits Enter).
    // We find out which SaleLine that row belongs to via row.Tag (set in
    // RefreshCart below), figure out which column changed, and write the
    // typed value back into the real object.
    private void GridCart_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        var row = gridCart.Rows[e.RowIndex];
        if (row.Tag is not SaleLine line) return;

        var columnName = gridCart.Columns[e.ColumnIndex].Name;
        var newValue = row.Cells[e.ColumnIndex].Value?.ToString() ?? "";

        switch (columnName)
        {
            case "Quantity":
                if (int.TryParse(newValue, out var qty))
                {
                    if (qty <= 0)
                    {
                        _sale.Lines.Remove(line);
                    }
                    else
                    {
                        line.Quantity = qty;
                    }
                }
                break;
            case "PriceEach":
                if (decimal.TryParse(newValue, out var price) && price >= 0)
                    line.PriceEach = price;
                break;
            case "Discount":
                if (decimal.TryParse(newValue, out var disc) && disc >= 0)
                    line.DiscountPercent = disc;
                break;
        }

        BeginInvoke(RefreshCart); // deferred, so we don't rebuild the grid mid-click // redraw everything, including the recalculated Total column and the label
    }

    private void BtnRemove_Click(object? sender, EventArgs e)
    {
        RemoveSelectedLine();
    }

    private void GridCart_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedLine();
            e.Handled = true;
        }
    }

    private void RemoveSelectedLine()
    {
        // CurrentRow (the row of whichever cell is active) works regardless of
        // selection mode - SelectedRows was staying empty because a plain cell
        // click only selects that cell, not the whole row.
        var row = gridCart.CurrentRow;
        if (row == null)
        {
            MessageBox.Show("Select an item in the cart first.", "No Item Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (row.Tag is SaleLine line)
        {
            _sale.Lines.Remove(line);
            RefreshCart();
        }
    }

    private decimal GetSubtotal() => _sale.Lines.Sum(l => l.PriceEach * l.Quantity * (1 - l.DiscountPercent / 100));

    private decimal GetTaxRate() => _storeSettings.Load().TaxRatePercent;

    private void UpdateChangeDue()
    {
        var subtotal = GetSubtotal();
        var tax = subtotal * (GetTaxRate() / 100);
        var total = subtotal + tax;

        if (decimal.TryParse(txtCashTendered.Text, out var tendered))
        {
            var change = tendered - total;
            lblChange.Text = change.ToString("C");
            lblChange.ForeColor = change < 0 ? Color.Red : Color.Black;
        }
        else
        {
            lblChange.Text = "";
        }
    }

    private void BtnComplete_Click(object? sender, EventArgs e)
    {
        if (_sale.Lines.Count == 0)
        {
            MessageBox.Show("Scan at least one item first.", "Empty Sale", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var warnings = _checkout.GetLowStockWarnings(_sale);
        if (warnings.Count > 0)
        {
            var message = "Some items are low on stock:\n\n" + string.Join("\n", warnings) + "\n\nComplete the sale anyway?";
            var result = MessageBox.Show(message, "Low Stock Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.No) return;
        }

        var subtotal = GetSubtotal();
        var tax = subtotal * (GetTaxRate() / 100);
        var total = subtotal + tax;
        decimal.TryParse(txtCashTendered.Text, out var tendered);
        var change = tendered - total;

        var store = _storeSettings.Load();
        var receiptLines = BuildReceipt(subtotal, tax, total, tendered, change);

        _checkout.CompleteSale(_sale);
        _sale = _checkout.StartSale();
        lblCustomer.Text = "Customer: Walk-in";
        txtCustomerSearch.Text = "";
        lstCustomerResults.Items.Clear();
        lstCustomerResults.Visible = false;
        customerPanel.Height = lstCustomerResults.Visible ? 120 : 60;
        txtCashTendered.Clear();
        RefreshCart();

        using var receiptForm = new ReceiptForm(receiptLines, store.StoreName, store.Address, store.Phone, store.LogoPath);
        receiptForm.ShowDialog(this);
    }

    // Just the itemized body - date, line items, totals - with amounts
    // right-aligned like a real receipt. The store name/address/phone/logo
    // are rendered separately as a styled header by ReceiptForm, not mixed
    // into this plain-text block.
    private const int ReceiptWidth = 34;

    private string BuildReceipt(decimal subtotal, decimal tax, decimal total, decimal tendered, decimal change)
    {
        var sb = new StringBuilder();
        sb.AppendLine(DateTime.Now.ToString("MM/dd/yyyy h:mm tt"));
        sb.AppendLine(new string('-', ReceiptWidth));

        foreach (var line in _sale.Lines)
        {
            sb.AppendLine($"{line.Cover.Issue.Series.Name} #{line.Cover.Issue.IssueNumber:0.##} [{line.Cover.CoverLetter}]");
            var qtyPrice = $"  {line.Quantity} x {line.PriceEach:C}";
            var lineTotal = (line.PriceEach * line.Quantity * (1 - line.DiscountPercent / 100)).ToString("C");
            sb.AppendLine(PadLine(qtyPrice, lineTotal));
        }

        sb.AppendLine(new string('-', ReceiptWidth));
        sb.AppendLine(PadLine("Subtotal", subtotal.ToString("C")));
        sb.AppendLine(PadLine("Tax", tax.ToString("C")));
        sb.AppendLine(PadLine("TOTAL", total.ToString("C")));

        if (tendered > 0)
        {
            sb.AppendLine(new string('-', ReceiptWidth));
            sb.AppendLine(PadLine("Tendered", tendered.ToString("C")));
            sb.AppendLine(PadLine("Change", change.ToString("C")));
        }

        return sb.ToString();
    }

    private static string PadLine(string left, string right)
    {
        var gap = Math.Max(1, ReceiptWidth - left.Length - right.Length);
        return left + new string(' ', gap) + right;
    }

    // No longer binds DataSource to an anonymous list. Instead, clears the
    // grid and rebuilds each row by hand, storing the real SaleLine on
    // row.Tag so GridCart_CellEndEdit can find it again later.
    private void RefreshCart()
    {
        gridCart.Rows.Clear();

        foreach (var line in _sale.Lines)
        {
            var rowIndex = gridCart.Rows.Add();
            var row = gridCart.Rows[rowIndex];

            row.Cells["Series"].Value = $"{line.Cover.Issue.Series.Name} ({line.Cover.Issue.Series.StartYear})";
            row.Cells["Issue"].Value = line.Cover.Issue.IssueNumber;
            row.Cells["Cover"].Value = line.Cover.CoverLetter;
            row.Cells["Quantity"].Value = line.Quantity;
            row.Cells["PriceEach"].Value = line.PriceEach.ToString("0.00");
            row.Cells["Discount"].Value = line.DiscountPercent.ToString("0.00");
            row.Cells["Total"].Value = (line.PriceEach * line.Quantity * (1 - line.DiscountPercent / 100)).ToString("C");

            row.Tag = line;
        }

        var subtotal = GetSubtotal();
        var tax = subtotal * (GetTaxRate() / 100);
        var total = subtotal + tax;

        lblSubtotal.Text = subtotal.ToString("C");
        lblTax.Text = tax.ToString("C");
        lblGrandTotal.Text = total.ToString("C");
        UpdateChangeDue();
    }

    private void SearchCustomers()
    {
        lstCustomerResults.Items.Clear();
        _customerResults.Clear();

        if (string.IsNullOrWhiteSpace(txtCustomerSearch.Text))
        {
            lstCustomerResults.Visible = false;
            customerPanel.Height = lstCustomerResults.Visible ? 120 : 60;
            return;
        }

        _customerResults = _customers.SearchCustomers(txtCustomerSearch.Text);
        foreach (var customer in _customerResults)
            lstCustomerResults.Items.Add(customer.Name);

        lstCustomerResults.Visible = lstCustomerResults.Items.Count > 0;
        customerPanel.Height = lstCustomerResults.Visible ? 120 : 60;
    }

    private void CustomerResult_Selected(object? sender, EventArgs e)
    {
        if (lstCustomerResults.SelectedIndex < 0) return;

        var selected = _customerResults[lstCustomerResults.SelectedIndex];
        _sale.CustomerId = selected.Id;
        lblCustomer.Text = $"Customer: {selected.Name}";

        txtCustomerSearch.Text = "";
        lstCustomerResults.Items.Clear();
        lstCustomerResults.Visible = false;
        customerPanel.Height = lstCustomerResults.Visible ? 120 : 60;
    }
}
