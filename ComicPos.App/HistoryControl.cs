using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class HistoryControl : UserControl
{
    private readonly ISalesHistoryService _history;
    private readonly ICheckoutService _checkout;
    private readonly Label lblBreadcrumb = new();
    private readonly Button btnBack = new();
    private readonly Button btnVoid = new();
    private readonly DataGridView grid = new();

    private string? _selectedSeries = null;
    private int? _selectedIssueId = null;
    private float? _selectedIssueNumber = null;

    public HistoryControl(ISalesHistoryService history, ICheckoutService checkout)
    {
        _history = history;
        _checkout = checkout;

        Dock = DockStyle.Fill;

        lblBreadcrumb.Dock = DockStyle.Top;
        // AutoSize, not a fixed Height - see InventoryControl's lblBreadcrumb
        // for why a hardcoded pixel value here clipped the text.
        lblBreadcrumb.AutoSize = true;
        lblBreadcrumb.TextAlign = ContentAlignment.MiddleLeft;
        lblBreadcrumb.Font = UiTheme.HeaderFont;
        lblBreadcrumb.ForeColor = UiTheme.PrimaryDark;
        lblBreadcrumb.Padding = new Padding(4, 0, 0, 0);

        btnBack.Dock = DockStyle.Top;
        btnBack.Text = "< Back";
        btnBack.Height = 30;
        btnBack.Click += (s, e) =>
        {
            if (_selectedIssueId != null)
                ShowIssueList(_selectedSeries!);
            else if (_selectedSeries != null)
                ShowSeriesList();
        };

        btnVoid.Dock = DockStyle.Top;
        btnVoid.Height = 30;
        btnVoid.Text = "Void Selected Sale";
        btnVoid.Visible = false;
        btnVoid.Click += BtnVoid_Click;

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellDoubleClick += Grid_CellDoubleClick;

        // Same Dock=Top stacking fix as InventoryControl: last-added ends
        // up closest to the top edge, so this is in reverse of the
        // intended reading order (title at the very top).
        Controls.Add(grid);
        Controls.Add(btnVoid);
        Controls.Add(btnBack);
        Controls.Add(lblBreadcrumb);

        ShowSeriesList();

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
    }

    private void ShowSeriesList()
    {
        _selectedSeries = null;
        _selectedIssueId = null;
        btnBack.Visible = false;
        btnVoid.Visible = false;
        lblBreadcrumb.Text = "All Series";

        grid.DataSource = null;
        grid.Columns.Clear();
        grid.AutoGenerateColumns = true;

        var seriesNames = _history.GetSeriesWithSales();

        grid.DataSource = seriesNames
            .Select(name => new { Series = name })
            .ToList();
    }

    private void ShowIssueList(string seriesName)
    {
        _selectedSeries = seriesName;
        _selectedIssueId = null;
        btnBack.Visible = true;
        btnVoid.Visible = false;
        lblBreadcrumb.Text = $"{seriesName} — Issues";

        grid.DataSource = null;
        grid.Columns.Clear();
        grid.AutoGenerateColumns = true;

        var issues = _history.GetIssuesWithSales(seriesName);

        grid.DataSource = issues
            .Select(i => new
            {
                i.Id,
                Issue = i.IssueNumber,
                i.Publisher
            })
            .ToList();
    }

    private void ShowSaleDetail(int issueId, float issueNumber)
    {
        _selectedIssueId = issueId;
        _selectedIssueNumber = issueNumber;
        btnBack.Visible = true;
        btnVoid.Visible = true;
        lblBreadcrumb.Text = $"{_selectedSeries} #{issueNumber:0.##} — Sales (select a row, then Void)";

        grid.DataSource = null;
        grid.Rows.Clear();
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cover", HeaderText = "Cover", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Quantity", HeaderText = "Quantity", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PriceEach", HeaderText = "Price Each", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Discount", HeaderText = "Discount", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SoldAt", HeaderText = "Sold At", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", ReadOnly = true });

        var sales = _history.GetSalesForIssue(issueId);

        foreach (var line in sales)
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Cover"].Value = line.Cover.CoverLetter;
            row.Cells["Quantity"].Value = line.Quantity;
            row.Cells["PriceEach"].Value = line.PriceEach.ToString("C");
            row.Cells["Discount"].Value = line.DiscountPercent.ToString("0.0") + "%";
            row.Cells["SoldAt"].Value = line.Sale.SoldAt.ToString("g");
            row.Cells["Status"].Value = line.Sale.IsVoided ? "VOIDED" : "";
            if (line.Sale.IsVoided)
                row.DefaultCellStyle.ForeColor = Color.Gray;
            row.Tag = line;
        }
    }

    private void BtnVoid_Click(object? sender, EventArgs e)
    {
        if (grid.SelectedRows.Count == 0)
        {
            MessageBox.Show("Select a sale row first.", "No Row Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (grid.SelectedRows[0].Tag is not SaleLine line) return;

        if (line.Sale.IsVoided)
        {
            MessageBox.Show("That sale is already voided.", "Already Voided", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Void this entire sale from {line.Sale.SoldAt:g}? This restores stock for every item on that sale.",
            "Void Sale", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        _checkout.VoidSale(line.SaleId);
        ShowSaleDetail(_selectedIssueId!.Value, _selectedIssueNumber!.Value);
    }

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;

        var row = grid.Rows[e.RowIndex];

        if (_selectedSeries == null)
        {
            var seriesName = row.Cells["Series"].Value?.ToString();
            if (seriesName != null)
                ShowIssueList(seriesName);
        }
        else if (_selectedIssueId == null)
        {
            if (int.TryParse(row.Cells["Id"].Value?.ToString(), out var issueId))
            {
                var issueValue = row.Cells["Issue"].Value;
                float issueNumber = issueValue != null ? Convert.ToSingle(issueValue) : 0f;
                ShowSaleDetail(issueId, issueNumber);
            }
        }
    }

    // Re-clicking the History tab while already on it (MainForm's tab
    // strip) jumps back out to the series list instead of leaving you
    // stuck wherever you'd drilled down to.
    public void ResetToTop() => ShowSeriesList();
}
