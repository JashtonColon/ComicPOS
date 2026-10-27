using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class CheckoutControl : UserControl
{
    private readonly ICheckoutService _checkout;
    private readonly ICustomerService _customers;
    private Sale _sale;
    private readonly TextBox txtBarcode = new();
    private readonly DataGridView gridCart = new();
    private readonly Label lblTotal = new();
    private readonly Button btnComplete = new();

    private readonly TextBox txtCustomerSearch = new();
    private readonly ListBox lstCustomerResults = new();
    private readonly Label lblCustomer = new();
    private List<Customer> _customerResults = new(); 

    public CheckoutControl(ICheckoutService checkout, ICustomerService customers)
    {
        _checkout = checkout;
        _customers = customers;
        _sale = _checkout.StartSale();

        Dock = DockStyle.Fill;

        txtBarcode.Dock = DockStyle.Top;
        txtBarcode.PlaceholderText = "Scan barcode...";

        ConfigureGridColumns();
        gridCart.Dock = DockStyle.Fill;
        gridCart.AllowUserToAddRows = false;
        gridCart.RowHeadersVisible = false;
        gridCart.CellEndEdit += GridCart_CellEndEdit;

        lblTotal.Dock = DockStyle.Bottom;
        lblTotal.TextAlign = ContentAlignment.MiddleRight;
        lblTotal.Height = 30;
        lblTotal.Text = "Total: $0.00";

        btnComplete.Dock = DockStyle.Bottom;
        btnComplete.Height = 35;
        btnComplete.Text = "Complete Sale";



        Controls.Add(gridCart);
        Controls.Add(lblTotal);
        Controls.Add(btnComplete);
        Controls.Add(txtBarcode);   

        txtBarcode.KeyDown += TxtBarcode_KeyDown;
        btnComplete.Click += BtnComplete_Click;

        RefreshCart();
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
                if (int.TryParse(newValue, out var qty) && qty > 0)
                    line.Quantity = qty;
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

    private void BtnComplete_Click(object? sender, EventArgs e)
    {
        var warnings = _checkout.GetLowStockWarnings(_sale);
        if (warnings.Count > 0)
        {
            var message = "Some items are low on stock:\n\n" + string.Join("\n", warnings) + "\n\nComplete the sale anyway?";
            var result = MessageBox.Show(message, "Low Stock Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.No) return;
        }

        _checkout.CompleteSale(_sale);
        _sale = _checkout.StartSale();
        RefreshCart();
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

        lblTotal.Text = "Total: " + _sale.Lines.Sum(l => l.PriceEach * l.Quantity * (1 - l.DiscountPercent / 100)).ToString("C");
    }
}