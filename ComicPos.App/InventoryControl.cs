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
    private float? _selectedIssueNumber = null;
    private readonly TextBox txtSearch = new();
    private readonly Button btnAddItem = new();
    private readonly PictureBox picCoverPreview = new();

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
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellDoubleClick += Grid_CellDoubleClick;
        grid.CellEndEdit += Grid_CellEndEdit;
        grid.SelectionChanged += Grid_SelectionChanged;

        picCoverPreview.Dock = DockStyle.Right;
        picCoverPreview.Width = 160;
        picCoverPreview.SizeMode = PictureBoxSizeMode.Zoom;
        picCoverPreview.BackColor = Color.White;
        picCoverPreview.BorderStyle = BorderStyle.FixedSingle;
        picCoverPreview.Visible = false;

        txtSearch.Dock = DockStyle.Top;
        txtSearch.PlaceholderText = "Search series name...";
        txtSearch.TextChanged += (s, e) => ShowSeriesList();

        btnAddItem.Dock = DockStyle.Top;
        btnAddItem.Height = 32;
        btnAddItem.Text = "+ Add New Item";
        btnAddItem.Click += BtnAddItem_Click;
        btnAddItem.Tag = "primary";

        Controls.Add(grid);
        Controls.Add(picCoverPreview);
        Controls.Add(lblBreadcrumb);
        Controls.Add(btnBack);
        Controls.Add(txtSearch);
        Controls.Add(btnAddItem);

        ShowSeriesList();

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
    }

    private void ShowSeriesList()
    {
        _selectedSeries = null;
        _selectedType = null;
        _selectedIssueId = null;
        _selectedIssueNumber = null;
        btnBack.Visible = false;
        lblBreadcrumb.Text = "All Series";
        picCoverPreview.Visible = false;

        grid.DataSource = null;
        grid.Rows.Clear();
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series", ReadOnly = true });

        var allSeries = _inventory.SearchSeriesForDisplay(txtSearch.Text);

        foreach (var series in allSeries)
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Series"].Value = $"{series.Name} ({series.StartYear})";
            row.Tag = series.Name;
        }
    }

    private void BtnAddItem_Click(object? sender, EventArgs e)
    {
        using var form = new AddItemForm(_inventory);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            txtSearch.Clear();
            ShowSeriesList();
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
        picCoverPreview.Visible = false;

        grid.DataSource = null;
        grid.Rows.Clear();
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Choice", HeaderText = "Choice", ReadOnly = true });

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
        picCoverPreview.Visible = false;

        grid.DataSource = null;
        grid.Rows.Clear();
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Issue", HeaderText = "Issue #", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Publisher", HeaderText = "Publisher", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "Total Stock", ReadOnly = true });

        var issues = _inventory.GetIssuesForSeries(seriesName, type);

        // row.Tag holds the real Issue entity, so drilling in never has to
        // parse the displayed text back into a number (IssueNumber is a
        // float - "1.5" annuals would fail an int.TryParse on the old path).
        foreach (var issue in issues.OrderBy(i => i.IssueNumber))
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Issue"].Value = issue.IssueNumber.ToString("0.##");
            row.Cells["Publisher"].Value = issue.Publisher;
            row.Cells["Total"].Value = issue.Covers.Sum(c => c.StockQuantity);
            row.Tag = issue;
        }
    }

    private void ShowCoverDetail(int issueId, float issueNumber)
    {
        _selectedIssueId = issueId;
        _selectedIssueNumber = issueNumber;
        btnBack.Visible = true;
        lblBreadcrumb.Text = $"{_selectedSeries} #{issueNumber:0.##} — Covers (double-click a cell to edit; set the real Barcode here when a book from New Books arrives)";
        picCoverPreview.Visible = true;
        picCoverPreview.Image = null;

        grid.DataSource = null;
        grid.Rows.Clear();
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cover", HeaderText = "Cover", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Barcode", HeaderText = "Barcode", ReadOnly = false });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "Price", ReadOnly = false });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Stock", HeaderText = "Stock", ReadOnly = false });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "Description", ReadOnly = false });

        var issues = _inventory.GetIssuesForSeries(_selectedSeries!, _selectedType!.Value);
        var issue = issues.FirstOrDefault(i => i.Id == issueId);
        if (issue == null) return;

        foreach (var cover in issue.Covers.OrderBy(c => c.CoverLetter))
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Cover"].Value = cover.CoverLetter;
            row.Cells["Barcode"].Value = cover.Barcode;
            row.Cells["Price"].Value = cover.Price.ToString("0.00");
            row.Cells["Stock"].Value = cover.StockQuantity;
            row.Cells["Description"].Value = cover.Description;
            row.Tag = cover;
        }
    }

    // Edits here save immediately (receiving a shipment bumps Stock, a
    // price correction updates Price) - there's no separate "save" step,
    // same as the editable cart grid in Checkout.
    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        var row = grid.Rows[e.RowIndex];
        if (row.Tag is not Cover cover) return;

        var columnName = grid.Columns[e.ColumnIndex].Name;
        var newValue = row.Cells[e.ColumnIndex].Value?.ToString() ?? "";

        // Comics that New Books adds automatically get a placeholder
        // "CV-..." barcode (not a real UPC - Comic Vine doesn't have one),
        // so scanning the real barcode off the actual book in Add New Item
        // won't find them. Editing Barcode here - typically when the book
        // physically arrives - is how that placeholder gets replaced with
        // the real one, after which Add New Item's barcode scan will match
        // it for future receiving/restocking.
        var barcodeChanged = false;
        var previousBarcode = cover.Barcode;

        switch (columnName)
        {
            case "Barcode":
                var trimmed = newValue.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    MessageBox.Show("Barcode can't be blank.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    BeginInvoke(() => ShowCoverDetail(_selectedIssueId!.Value, _selectedIssueNumber!.Value));
                    return;
                }
                cover.Barcode = trimmed;
                barcodeChanged = trimmed != previousBarcode;
                break;
            case "Price":
                if (decimal.TryParse(newValue, out var price) && price >= 0)
                    cover.Price = price;
                break;
            case "Stock":
                if (int.TryParse(newValue, out var stock) && stock >= 0)
                    cover.StockQuantity = stock;
                break;
            case "Description":
                cover.Description = string.IsNullOrWhiteSpace(newValue) ? null : newValue;
                break;
        }

        try
        {
            _inventory.UpdateCover(cover);
        }
        catch (Exception)
        {
            // Barcode has a unique index (ComicDbContext) - most likely
            // cause here is typing a barcode another cover already has.
            // Revert in memory and in the (failed, now stale-tracked) DB
            // context so this doesn't silently resurface as a duplicate
            // on some later unrelated save, same class of bug fixed
            // earlier in AutoFetchService.
            if (barcodeChanged)
                cover.Barcode = previousBarcode;
            _inventory.ClearFailedChanges();
            MessageBox.Show(
                barcodeChanged
                    ? "That barcode is already used by another item. Double-check the book and try again."
                    : "Couldn't save that change.",
                "Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        BeginInvoke(() => ShowCoverDetail(_selectedIssueId!.Value, _selectedIssueNumber!.Value));
    }

    private void Grid_SelectionChanged(object? sender, EventArgs e)
    {
        if (!picCoverPreview.Visible) return;

        picCoverPreview.Image?.Dispose();
        picCoverPreview.Image = null;

        if (grid.CurrentRow?.Tag is not Cover cover) return;
        if (string.IsNullOrWhiteSpace(cover.CoverImagePath) || !File.Exists(cover.CoverImagePath)) return;

        try
        {
            using var fs = new FileStream(cover.CoverImagePath, FileMode.Open, FileAccess.Read);
            picCoverPreview.Image = Image.FromStream(fs);
        }
        catch
        {
            // Missing/corrupt cached image - just show no preview.
        }
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
            if (row.Tag is Issue issue)
                ShowCoverDetail(issue.Id, issue.IssueNumber);
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