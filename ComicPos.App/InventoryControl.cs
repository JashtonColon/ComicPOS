using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class InventoryControl : UserControl
{
    private readonly IInventoryService _inventory;
    private readonly TextBox txtSearch = new();
    private readonly DataGridView gridIssues = new();
    private readonly DataGridView gridCovers = new();
    private List<Issue> _results = new();

    public InventoryControl(IInventoryService inventory)
    {
        _inventory = inventory;

        Dock = DockStyle.Fill;

        // Two grids stacked in a split container
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal
        };

        ConfigureGrid(gridIssues);
        ConfigureGrid(gridCovers);
        split.Panel1.Controls.Add(gridIssues);
        split.Panel2.Controls.Add(gridCovers);

        txtSearch.Dock = DockStyle.Top;
        txtSearch.PlaceholderText = "Search series name...";

        Controls.Add(split);
        Controls.Add(txtSearch);

        txtSearch.TextChanged += (s, e) => Search();
        gridIssues.SelectionChanged += (s, e) => ShowCovers();

        Search();
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.RowHeadersVisible = false;
    }

    private void Search()
    {
        _results = _inventory.SearchIssues(txtSearch.Text);

        gridIssues.DataSource = _results
            .Select(i => new
            {
                i.Id,
                Series = i.Series.Name,
                Issue = i.IssueNumber,
                Publisher = i.Publisher,
                Total = i.Covers.Sum(c => c.StockQuantity)
            })
            .ToList();

        ShowCovers();
    }

    private void ShowCovers()
    {
        if (gridIssues.CurrentRow == null)
        {
            gridCovers.DataSource = null;
            return;
        }

        var id = Convert.ToInt32(gridIssues.CurrentRow.Cells["Id"].Value);
        var issue = _results.FirstOrDefault(i => i.Id == id);

        if (issue == null)
        {
            gridCovers.DataSource = null;
            return;
        }

        gridCovers.DataSource = issue.Covers
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
    public void ReloadData()
    {
        Search();
    }
}