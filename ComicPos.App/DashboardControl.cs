using ComicPos.Core.Services;

namespace ComicPos.App;

public class DashboardControl : UserControl
{
    private readonly IDashboardService _dashboard;

    private readonly Label lblToday = new();
    private readonly Label lblWeek = new();
    private readonly Label lblMonth = new();
    private readonly Button btnRefresh = new();

    private readonly DataGridView gridTopSellers = new();
    private readonly DataGridView gridLowStock = new();

    public DashboardControl(IDashboardService dashboard)
    {
        _dashboard = dashboard;

        Dock = DockStyle.Fill;

        btnRefresh.Dock = DockStyle.Top;
        btnRefresh.Height = 32;
        btnRefresh.Text = "Refresh";
        btnRefresh.Click += (s, e) => RefreshAll();

        var revenuePanel = new Panel { Dock = DockStyle.Top, Height = 70 };
        var revenueFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8)
        };

        void StyleRevenueLabel(Label label)
        {
            label.AutoSize = false;
            label.Width = 220;
            label.Height = 50;
            label.Font = new Font(label.Font.FontFamily, 11, FontStyle.Bold);
            label.TextAlign = ContentAlignment.MiddleLeft;
        }

        StyleRevenueLabel(lblToday);
        StyleRevenueLabel(lblWeek);
        StyleRevenueLabel(lblMonth);

        revenueFlow.Controls.Add(lblToday);
        revenueFlow.Controls.Add(lblWeek);
        revenueFlow.Controls.Add(lblMonth);
        revenuePanel.Controls.Add(revenueFlow);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 400
        };

        var topSellersLabel = new Label { Dock = DockStyle.Top, Height = 26, Text = "Top Sellers (last 30 days)", Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        ConfigureGrid(gridTopSellers);
        gridTopSellers.AutoGenerateColumns = false;
        gridTopSellers.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series", ReadOnly = true });
        gridTopSellers.Columns.Add(new DataGridViewTextBoxColumn { Name = "Qty", HeaderText = "Units Sold", ReadOnly = true });
        split.Panel1.Controls.Add(gridTopSellers);
        split.Panel1.Controls.Add(topSellersLabel);

        var lowStockLabel = new Label { Dock = DockStyle.Top, Height = 26, Text = "Low Stock (2 or fewer)", Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        ConfigureGrid(gridLowStock);
        gridLowStock.AutoGenerateColumns = false;
        gridLowStock.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series", ReadOnly = true });
        gridLowStock.Columns.Add(new DataGridViewTextBoxColumn { Name = "Issue", HeaderText = "Issue #", ReadOnly = true });
        gridLowStock.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cover", HeaderText = "Cover", ReadOnly = true });
        gridLowStock.Columns.Add(new DataGridViewTextBoxColumn { Name = "Stock", HeaderText = "Stock", ReadOnly = true });
        split.Panel2.Controls.Add(gridLowStock);
        split.Panel2.Controls.Add(lowStockLabel);

        Controls.Add(split);
        Controls.Add(revenuePanel);
        Controls.Add(btnRefresh);

        RefreshAll();
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
    }

    public void RefreshAll()
    {
        var now = DateTime.Now;
        var startOfToday = now.Date;
        var startOfWeek = now.Date.AddDays(-(int)now.DayOfWeek);
        var startOfMonth = new DateTime(now.Year, now.Month, 1);

        lblToday.Text = "Today: " + _dashboard.GetRevenue(startOfToday, now).ToString("C");
        lblWeek.Text = "This Week: " + _dashboard.GetRevenue(startOfWeek, now).ToString("C");
        lblMonth.Text = "This Month: " + _dashboard.GetRevenue(startOfMonth, now).ToString("C");

        gridTopSellers.Rows.Clear();
        foreach (var entry in _dashboard.GetTopSellingSeries(30, 10))
        {
            var rowIndex = gridTopSellers.Rows.Add();
            gridTopSellers.Rows[rowIndex].Cells["Series"].Value = entry.SeriesName;
            gridTopSellers.Rows[rowIndex].Cells["Qty"].Value = entry.QuantitySold;
        }

        gridLowStock.Rows.Clear();
        foreach (var entry in _dashboard.GetLowStock(2))
        {
            var rowIndex = gridLowStock.Rows.Add();
            var row = gridLowStock.Rows[rowIndex];
            row.Cells["Series"].Value = $"{entry.SeriesName} ({entry.StartYear})";
            row.Cells["Issue"].Value = entry.IssueNumber.ToString("0.##");
            row.Cells["Cover"].Value = entry.CoverLetter;
            row.Cells["Stock"].Value = entry.StockQuantity;
        }
    }
}
