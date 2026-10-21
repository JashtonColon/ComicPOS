using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class HistoryControl : UserControl
{
    private readonly ISalesHistoryService _history;
    private readonly Label lblBreadcrumb = new();
    private readonly Button btnBack = new();
    private readonly DataGridView grid = new();

    private string? _selectedSeries = null;
    private int? _selectedIssueId = null;

    public HistoryControl(ISalesHistoryService history)
    {
        _history = history;

        Dock = DockStyle.Fill;

        lblBreadcrumb.Dock = DockStyle.Top;
        lblBreadcrumb.Height = 30;
        lblBreadcrumb.TextAlign = ContentAlignment.MiddleLeft;

        btnBack.Dock = DockStyle.Top;
        btnBack.Text = "< Back";
        btnBack.Click += (s, e) =>
        {
            if (_selectedIssueId != null)
            {
                // leaving sale detail, go back to this series' issue list
                ShowIssueList(_selectedSeries!);
            }
            else if (_selectedSeries != null)
            {
                // leaving issue list, go back to the series list
                ShowSeriesList();
            }
        };

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellDoubleClick += Grid_CellDoubleClick;

        Controls.Add(grid);
        Controls.Add(lblBreadcrumb);
        Controls.Add(btnBack);

        ShowSeriesList();
    }

    private void ShowSeriesList()
    {
        _selectedSeries = null;
        _selectedIssueId = null;
        btnBack.Visible = false;
        lblBreadcrumb.Text = "All Series";

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
        lblBreadcrumb.Text = $"{seriesName} — Issues";

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

    private void ShowSaleDetail(int issueId, int issueNumber)
    {
        _selectedIssueId = issueId;
        btnBack.Visible = true;
        lblBreadcrumb.Text = $"{_selectedSeries} #{issueNumber} — Sales";

        var sales = _history.GetSalesForIssue(issueId);

        grid.DataSource = sales
            .Select(l => new
            {
                Cover = l.Cover.CoverLetter,
                l.Quantity,
                PriceEach = l.PriceEach.ToString("C"),
                Discount = l.DiscountPercent.ToString("0.0") + "%",
                SoldAt = l.Sale.SoldAt.ToString("g")
            })
            .ToList();
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
            if (int.TryParse(row.Cells["Id"].Value?.ToString(), out var issueId) &&
                int.TryParse(row.Cells["Issue"].Value?.ToString(), out var issueNumber))
            {
                ShowSaleDetail(issueId, issueNumber);
            }
        }
    }
}