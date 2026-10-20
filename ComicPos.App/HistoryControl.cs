using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class HistoryControl : UserControl
{
    private readonly ISalesHistoryService _history;
    private readonly TextBox txtSearch = new();
    private readonly DataGridView gridHistory = new();

    public HistoryControl(ISalesHistoryService history)
    {
        _history = history;

        Dock = DockStyle.Fill;

        txtSearch.Dock = DockStyle.Top;
        txtSearch.PlaceholderText = "Search series name...";

        gridHistory.Dock = DockStyle.Fill;
        gridHistory.ReadOnly = true;
        gridHistory.AllowUserToAddRows = false;
        gridHistory.AllowUserToDeleteRows = false;
        gridHistory.MultiSelect = false;
        gridHistory.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        gridHistory.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        gridHistory.RowHeadersVisible = false;

        Controls.Add(gridHistory);
        Controls.Add(txtSearch);

        txtSearch.TextChanged += (s, e) => Search();

        Search();
    }

    private void Search()
    {
        var results = _history.GetHistoryForSeries(txtSearch.Text);

        gridHistory.DataSource = results
            .Select(l => new
            {
                Series = l.Cover.Issue.Series.Name,
                Issue = l.Cover.Issue.IssueNumber,
                Cover = l.Cover.CoverLetter,
                l.Quantity,
                PriceEach = l.PriceEach.ToString("C"),
                Discount = l.DiscountPercent.ToString("0.0") + "%",
                SoldAt = l.Sale.SoldAt.ToString("g")
            })
            .ToList();
    }
}