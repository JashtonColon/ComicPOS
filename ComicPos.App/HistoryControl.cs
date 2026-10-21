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

    public HistoryControl(ISalesHistoryService history)
    {
        _history = history;

        Dock = DockStyle.Fill;

        lblBreadcrumb.Dock = DockStyle.Top;
        lblBreadcrumb.Height = 30;
        lblBreadcrumb.TextAlign = ContentAlignment.MiddleLeft;

        btnBack.Dock = DockStyle.Top;
        btnBack.Text = "< Back";
        btnBack.Click += (s, e) => ShowSeriesList();

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

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return; // header row, ignore

        if (_selectedSeries == null)
        {
            var row = grid.Rows[e.RowIndex];
            var seriesName = row.Cells["Series"].Value?.ToString();
            if (seriesName != null)
                ShowIssueList(seriesName);
        }
        else
        {
            // next session's work: drill into the clicked issue's covers/sales
        }
    }
}