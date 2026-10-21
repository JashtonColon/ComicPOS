using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class InventoryControl : UserControl
{
    private readonly IInventoryService _inventory;
    private readonly Label lblBreadcrumb = new();
    private readonly Button btnBack = new();
    private readonly DataGridView grid = new();

    private string? _selectedSeries = null;
    private IssueType? _selectedType = null;
    private int? _selectedIssueId = null;
    private int? _selectedIssueNumber = null;
    private readonly TextBox txtSearch = new();

    public InventoryControl(IInventoryService inventory)
    {
        _inventory = inventory;

        Dock = DockStyle.Fill;

        lblBreadcrumb.Dock = DockStyle.Top;
        lblBreadcrumb.Height = 30; //"all series" in the inverntory screen 
        lblBreadcrumb.TextAlign = ContentAlignment.MiddleLeft;

        btnBack.Dock = DockStyle.Top;
        btnBack.Height = 30;
        btnBack.Text = "< Back";
        btnBack.Click += (s, e) =>
        {
            if (_selectedIssueId != null)
                ShowIssueList(_selectedSeries!, _selectedType!.Value);
            else if (_selectedType != null)
                ShowTypeChoice(_selectedSeries!);
            else if (_selectedSeries != null)
                ShowSeriesList();
        };

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellDoubleClick += Grid_CellDoubleClick;

        txtSearch.Dock = DockStyle.Top;
        txtSearch.PlaceholderText = "Search series name...";
        txtSearch.TextChanged += (s, e) => ShowSeriesList();

        Controls.Add(grid);
        Controls.Add(lblBreadcrumb);
        Controls.Add(btnBack);
        Controls.Add(txtSearch);

        ShowSeriesList();
    }

    private void ShowSeriesList()
    {
        _selectedSeries = null;
        _selectedType = null;
        _selectedIssueId = null;
        _selectedIssueNumber = null;
        btnBack.Visible = false;
        lblBreadcrumb.Text = "All Series";

        grid.DataSource = null;
        grid.Rows.Clear();
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series" });

        var allSeries = _inventory.SearchSeriesForDisplay(txtSearch.Text);

        foreach (var series in allSeries)
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Series"].Value = $"{series.Name} ({series.StartYear})";
            row.Tag = series.Name;
        }
    }

    private void ShowTypeChoice(string seriesName)
    {
        _selectedSeries = seriesName;
        _selectedType = null;
        _selectedIssueId = null;
        _selectedIssueNumber = null;
        btnBack.Visible = true;
        lblBreadcrumb.Text = $"{seriesName} — Choose Type";

        grid.DataSource = null;
        grid.Rows.Clear();
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Choice", HeaderText = "Choice" });

        var choices = new[] { "Single Issues", "Collected Editions" };
        foreach (var choice in choices)
        {
            var rowIndex = grid.Rows.Add();
            grid.Rows[rowIndex].Cells["Choice"].Value = choice;
        }
    }

    private void ShowIssueList(string seriesName, IssueType type)
    {
        _selectedSeries = seriesName;
        _selectedType = type;
        _selectedIssueId = null;
        _selectedIssueNumber = null;
        btnBack.Visible = true;
        lblBreadcrumb.Text = $"{seriesName} — {(type == IssueType.SingleIssue ? "Single Issues" : "Collected Editions")}";

        grid.Columns.Clear();
        grid.AutoGenerateColumns = true;

        var issues = _inventory.GetIssuesForSeries(seriesName, type);

        grid.DataSource = issues
            .Select(i => new
            {
                i.Id,
                Issue = i.IssueNumber,
                i.Publisher,
                Total = i.Covers.Sum(c => c.StockQuantity)
            })
            .ToList();
    }

    private void ShowCoverDetail(int issueId, int issueNumber)
    {
        _selectedIssueId = issueId;
        _selectedIssueNumber = issueNumber;
        btnBack.Visible = true;
        lblBreadcrumb.Text = $"{_selectedSeries} #{issueNumber} — Covers";

        grid.Columns.Clear();
        grid.AutoGenerateColumns = true;

        var issues = _inventory.GetIssuesForSeries(_selectedSeries!, _selectedType!.Value);
        var issue = issues.FirstOrDefault(i => i.Id == issueId);

        if (issue == null)
        {
            grid.DataSource = null;
            return;
        }

        grid.DataSource = issue.Covers
            .OrderBy(c => c.CoverLetter)
            .Select(c => new
            {
                Cover = c.CoverLetter,
                c.Barcode,
                c.Price,
                Stock = c.StockQuantity,
                c.Description
            })
            .ToList();
    }

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;

        var row = grid.Rows[e.RowIndex];

        if (_selectedSeries == null)
        {
            var seriesName = row.Tag as string;
            if (seriesName != null)
                ShowTypeChoice(seriesName);
        }
        else if (_selectedType == null)
        {
            var choice = row.Cells["Choice"].Value?.ToString();
            if (choice == "Single Issues")
                ShowIssueList(_selectedSeries, IssueType.SingleIssue);
            else if (choice == "Collected Editions")
                ShowIssueList(_selectedSeries, IssueType.CollectedEdition);
        }
        else if (_selectedIssueId == null)
        {
            if (int.TryParse(row.Cells["Id"].Value?.ToString(), out var issueId) &&
                int.TryParse(row.Cells["Issue"].Value?.ToString(), out var issueNumber))
            {
                ShowCoverDetail(issueId, issueNumber);
            }
        }
    }

    public void ReloadData()
    {
        if (_selectedIssueId != null)
            ShowCoverDetail(_selectedIssueId.Value, _selectedIssueNumber!.Value);
        else if (_selectedType != null)
            ShowIssueList(_selectedSeries!, _selectedType.Value);
        else if (_selectedSeries != null)
            ShowTypeChoice(_selectedSeries);
        else
            ShowSeriesList();
    }
}