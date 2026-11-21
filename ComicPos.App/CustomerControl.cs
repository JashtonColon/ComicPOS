using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class CustomerControl : UserControl
{
    private enum Screen { List, Detail, Edit, HistorySeries, HistoryIssues, HistoryLines, PullList }

    private readonly ICustomerService _customers;
    private readonly ISalesHistoryService _history;
    private readonly IPullListService _pullLists;
    private readonly IInventoryService _inventory;
    private readonly ICheckoutService _checkout;

    // Shared chrome
    private readonly TextBox txtSearch = new();
    private readonly Button btnBack = new();
    private readonly DataGridView grid = new();
    private readonly Panel addPanel;

    // Customer list "add new" button - opens AddCustomerForm as a popup
    // instead of a permanently-docked row of textboxes (that row's fixed
    // Height=35 stopped being tall enough once buttons grew a taller
    // minimum height, and the user asked for a popup instead anyway).
    private readonly Button btnAdd = new();
    private readonly Button btnVoidSale = new();

    // Detail / Edit screen
    private readonly Panel detailPanel;
    private readonly Panel viewPanel;
    private readonly Label lblDetailHeader = new();
    private readonly Label lblPullListHeader = new();
    private readonly Label lblDetailEmail = new();
    private readonly Label lblDetailPhone = new();
    private readonly Button btnEdit = new();
    private readonly Button btnSaleHistory = new();
    private readonly Button btnPullList = new();
    private readonly Button btnToggleActive = new();

    private readonly Panel editPanel;
    private readonly TextBox txtEditName = new();
    private readonly TextBox txtEditEmail = new();
    private readonly TextBox txtEditPhone = new();
    private readonly Button btnSaveEdit = new();
    private readonly Button btnCancelEdit = new();

    // Pull list screen - "add a series" panel, shown below the grid of current entries
    private readonly Panel pullListAddPanel;
    private readonly TextBox txtPullSeriesSearch = new();
    private readonly Button btnAddToPullList = new();
    private readonly ListBox lstPullSeriesResults = new();
    private List<Series> _pullSeriesResults = new();
    private Series? _selectedSeriesForPullList;

    private Screen _screen = Screen.List;
    private Customer? _selectedCustomer;
    private List<SaleLine> _customerHistory = new();
    private string? _selectedSeriesName;
    private float? _selectedIssueNumber;

    public CustomerControl(ICustomerService customers, ISalesHistoryService history, IPullListService pullLists, IInventoryService inventory, ICheckoutService checkout)
    {
        _customers = customers;
        _history = history;
        _pullLists = pullLists;
        _inventory = inventory;
        _checkout = checkout;

        Dock = DockStyle.Fill;

        // --- shared search box, used by List and HistorySeries screens ---
        txtSearch.Dock = DockStyle.Top;
        txtSearch.PlaceholderText = "Search...";
        txtSearch.TextChanged += (s, e) =>
        {
            if (_screen == Screen.List) ShowList();
            else if (_screen == Screen.HistorySeries) ShowHistorySeries();
        };

        // --- back button, behavior depends on current screen ---
        btnBack.Dock = DockStyle.Top;
        btnBack.Height = 30;
        btnBack.Text = "< Back";
        btnBack.Visible = false;
        btnBack.Click += BtnBack_Click;

        btnVoidSale.Dock = DockStyle.Top;
        btnVoidSale.Height = 30;
        btnVoidSale.Text = "Void Selected Sale";
        btnVoidSale.Visible = false;
        btnVoidSale.Click += BtnVoidSale_Click;

        // --- grid, used by List, HistorySeries, HistoryIssues, HistoryLines, PullList ---
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellDoubleClick += Grid_CellDoubleClick;

        // --- add-customer row, List screen only ---
        addPanel = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(10) };
        btnAdd.Dock = DockStyle.Fill;
        btnAdd.Text = "+ Add Customer";
        btnAdd.Click += AddCustomer_Click;
        btnAdd.Tag = "primary";

        addPanel.Controls.Add(btnAdd);

        // --- detail/edit panel (Fill, overlaps grid, toggled via Visible) ---
        detailPanel = new Panel { Dock = DockStyle.Fill, Visible = false };

        viewPanel = new Panel { Dock = DockStyle.Fill };
        lblDetailHeader.Dock = DockStyle.Top;
        lblDetailHeader.Height = 48;
        lblDetailHeader.Font = UiTheme.HeaderFont;
        lblDetailHeader.ForeColor = UiTheme.PrimaryDark;
        lblDetailHeader.Padding = new Padding(8, 8, 0, 0);
        lblDetailHeader.AutoSize = false;
        lblDetailHeader.TextAlign = ContentAlignment.MiddleLeft;

        lblDetailEmail.Dock = DockStyle.Top;
        lblDetailEmail.Height = 26;
        lblDetailEmail.Padding = new Padding(8, 0, 0, 0);

        lblDetailPhone.Dock = DockStyle.Top;
        lblDetailPhone.Height = 26;
        lblDetailPhone.Padding = new Padding(8, 0, 0, 0);

        // The Pull List screen reuses the shared `grid` (just repopulated
        // with pull-list columns) with detailPanel hidden, so none of
        // the customer-detail header labels above are on screen there -
        // nothing previously said whose pull list was even being looked
        // at.
        lblPullListHeader.Dock = DockStyle.Top;
        // AutoSize, not a fixed Height - see InventoryControl's lblBreadcrumb
        // for why a hardcoded pixel value here clipped the text.
        lblPullListHeader.AutoSize = true;
        lblPullListHeader.Font = UiTheme.HeaderFont;
        lblPullListHeader.ForeColor = UiTheme.PrimaryDark;
        lblPullListHeader.Padding = new Padding(8, 0, 0, 0);
        lblPullListHeader.TextAlign = ContentAlignment.MiddleLeft;
        lblPullListHeader.Visible = false;

        var actionFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 8, 0, 0)
        };
        btnEdit.Text = "Edit";
        btnEdit.AutoSize = true;
        btnEdit.Width = 90;
        btnEdit.Height = 30;
        btnEdit.Click += (s, e) => ShowEdit();

        btnSaleHistory.Text = "Sale History";
        btnSaleHistory.AutoSize = true;
        btnSaleHistory.Width = 130;
        btnSaleHistory.Height = 30;
        btnSaleHistory.Margin = new Padding(8, 0, 0, 0);
        btnSaleHistory.Click += (s, e) =>
        {
            if (_selectedCustomer == null) return;
            _customerHistory = _history.GetHistoryForCustomer(_selectedCustomer.Id);
            _selectedSeriesName = null;
            _selectedIssueNumber = null;
            ShowHistorySeries();
        };

        btnPullList.Text = "Pull List";
        btnPullList.AutoSize = true;
        btnPullList.Width = 100;
        btnPullList.Height = 30;
        btnPullList.Margin = new Padding(8, 0, 0, 0);
        btnPullList.Click += (s, e) => ShowPullList();

        btnToggleActive.AutoSize = true;
        btnToggleActive.Width = 110;
        btnToggleActive.Height = 30;
        btnToggleActive.Margin = new Padding(8, 0, 0, 0);
        btnToggleActive.Click += BtnToggleActive_Click;

        actionFlow.Controls.Add(btnEdit);
        actionFlow.Controls.Add(btnSaleHistory);
        actionFlow.Controls.Add(btnPullList);
        actionFlow.Controls.Add(btnToggleActive);

        // Reverse add order so the header ends up at the very top (see project convention).
        viewPanel.Controls.Add(actionFlow);
        viewPanel.Controls.Add(lblDetailPhone);
        viewPanel.Controls.Add(lblDetailEmail);
        viewPanel.Controls.Add(lblDetailHeader);

        // --- edit sub-panel ---
        editPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        txtEditName.Dock = DockStyle.Top;
        txtEditName.Height = 28;
        txtEditName.Margin = new Padding(8);
        txtEditName.PlaceholderText = "Name";

        txtEditEmail.Dock = DockStyle.Top;
        txtEditEmail.Height = 28;
        txtEditEmail.PlaceholderText = "Email";

        txtEditPhone.Dock = DockStyle.Top;
        txtEditPhone.Height = 28;
        txtEditPhone.PlaceholderText = "Phone";

        var editActionFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 8, 0, 0)
        };
        btnSaveEdit.Text = "Save";
        btnSaveEdit.Width = 90;
        btnSaveEdit.Height = 30;
        btnSaveEdit.Click += BtnSaveEdit_Click;
        btnSaveEdit.Tag = "primary";

        btnCancelEdit.Text = "Cancel";
        btnCancelEdit.Width = 90;
        btnCancelEdit.Height = 30;
        btnCancelEdit.Margin = new Padding(8, 0, 0, 0);
        btnCancelEdit.Click += (s, e) => ShowDetail();

        editActionFlow.Controls.Add(btnSaveEdit);
        editActionFlow.Controls.Add(btnCancelEdit);

        // Reverse add order again so Name ends up on top, fields in order, buttons below.
        editPanel.Controls.Add(editActionFlow);
        editPanel.Controls.Add(txtEditPhone);
        editPanel.Controls.Add(txtEditEmail);
        editPanel.Controls.Add(txtEditName);

        detailPanel.Controls.Add(editPanel);
        detailPanel.Controls.Add(viewPanel);

        // --- pull list "add a series" panel, Bottom, PullList screen only ---
        pullListAddPanel = new Panel { Dock = DockStyle.Bottom, Height = 150, Visible = false };

        // Grown from 32 - same reason as addPanel/ImportControl/FocReportControl:
        // not tall enough for the taller, rounded buttons from UiTheme.StyleButton.
        var pullTopRow = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(0, 6, 0, 6) };
        txtPullSeriesSearch.Dock = DockStyle.Fill;
        txtPullSeriesSearch.PlaceholderText = "Search a series to add...";
        txtPullSeriesSearch.TextChanged += PullSeriesSearch_TextChanged;

        btnAddToPullList.Dock = DockStyle.Right;
        btnAddToPullList.Width = 90;
        btnAddToPullList.Text = "Add";
        btnAddToPullList.Click += BtnAddToPullList_Click;
        btnAddToPullList.Tag = "primary";

        pullTopRow.Controls.Add(txtPullSeriesSearch);
        pullTopRow.Controls.Add(btnAddToPullList);

        lstPullSeriesResults.Dock = DockStyle.Fill;
        lstPullSeriesResults.SelectedIndexChanged += (s, e) =>
        {
            _selectedSeriesForPullList = lstPullSeriesResults.SelectedIndex >= 0 && lstPullSeriesResults.SelectedIndex < _pullSeriesResults.Count
                ? _pullSeriesResults[lstPullSeriesResults.SelectedIndex]
                : null;
        };
        // Double-clicking a result is the normal Windows way of saying
        // "use this one" - fill the search box with it instead of leaving
        // the box showing whatever partial text was typed, while keeping
        // the result list exactly as-is (still showing this item picked)
        // so the existing Add button still works right afterward. Swap
        // the TextChanged handler off first so this doesn't re-trigger a
        // fresh search against the full "Name (Year)" text and wipe the
        // very selection it just set.
        lstPullSeriesResults.DoubleClick += (s, e) =>
        {
            if (lstPullSeriesResults.SelectedIndex < 0 || lstPullSeriesResults.SelectedIndex >= _pullSeriesResults.Count) return;

            var chosen = _pullSeriesResults[lstPullSeriesResults.SelectedIndex];
            var label = $"{chosen.Name} ({chosen.StartYear})";

            txtPullSeriesSearch.TextChanged -= PullSeriesSearch_TextChanged;
            txtPullSeriesSearch.Text = label;
            txtPullSeriesSearch.SelectionStart = label.Length;
            txtPullSeriesSearch.TextChanged += PullSeriesSearch_TextChanged;

            _selectedSeriesForPullList = chosen;
        };

        pullListAddPanel.Controls.Add(lstPullSeriesResults);
        pullListAddPanel.Controls.Add(pullTopRow);

        Controls.Add(grid);
        Controls.Add(detailPanel);
        Controls.Add(addPanel);
        Controls.Add(pullListAddPanel);
        Controls.Add(lblPullListHeader);
        Controls.Add(btnVoidSale);
        Controls.Add(btnBack);
        Controls.Add(txtSearch);

        ShowList();

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
    }

    // ----- List screen -----

    private void ShowList()
    {
        _screen = Screen.List;
        _selectedCustomer = null;

        btnBack.Visible = false;
        txtSearch.Visible = true;
        addPanel.Visible = true;
        pullListAddPanel.Visible = false;
        btnVoidSale.Visible = false;
        grid.Visible = true;
        detailPanel.Visible = false;
        lblPullListHeader.Visible = false;

        grid.DataSource = null;
        grid.Columns.Clear();
        grid.AutoGenerateColumns = true;

        grid.DataSource = _customers.SearchCustomers(txtSearch.Text)
            .Select(c => new { c.Id, c.Name, c.Email, c.Phone })
            .ToList();
    }

    private void AddCustomer_Click(object? sender, EventArgs e)
    {
        using var form = new AddCustomerForm();
        if (form.ShowDialog(FindForm()) != DialogResult.OK) return;

        _customers.AddCustomer(form.CustomerName, form.Email, form.Phone);

        ShowList();
    }

    // ----- Detail / Edit screens -----

    private void ShowDetail()
    {
        if (_selectedCustomer == null) { ShowList(); return; }

        _screen = Screen.Detail;

        btnBack.Visible = true;
        txtSearch.Visible = false;
        addPanel.Visible = false;
        pullListAddPanel.Visible = false;
        btnVoidSale.Visible = false;
        grid.Visible = false;
        detailPanel.Visible = true;
        viewPanel.Visible = true;
        editPanel.Visible = false;
        lblPullListHeader.Visible = false;

        lblDetailHeader.Text = _selectedCustomer.Name;
        lblDetailEmail.Text = "Email: " + (_selectedCustomer.Email ?? "(none)");
        lblDetailPhone.Text = "Phone: " + (_selectedCustomer.Phone ?? "(none)");
        btnToggleActive.Text = _selectedCustomer.IsActive ? "Deactivate" : "Activate";
    }

    private void ShowEdit()
    {
        if (_selectedCustomer == null) return;

        _screen = Screen.Edit;
        viewPanel.Visible = false;
        editPanel.Visible = true;

        txtEditName.Text = _selectedCustomer.Name;
        txtEditEmail.Text = _selectedCustomer.Email ?? "";
        txtEditPhone.Text = _selectedCustomer.Phone ?? "";
    }

    private void BtnSaveEdit_Click(object? sender, EventArgs e)
    {
        if (_selectedCustomer == null) return;

        if (string.IsNullOrWhiteSpace(txtEditName.Text))
        {
            MessageBox.Show("Name is required.", "Missing Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _selectedCustomer.Name = txtEditName.Text;
        _selectedCustomer.Email = string.IsNullOrWhiteSpace(txtEditEmail.Text) ? null : txtEditEmail.Text;
        _selectedCustomer.Phone = string.IsNullOrWhiteSpace(txtEditPhone.Text) ? null : txtEditPhone.Text;

        _customers.UpdateCustomer(_selectedCustomer);
        ShowDetail();
    }

    private void BtnToggleActive_Click(object? sender, EventArgs e)
    {
        if (_selectedCustomer == null) return;

        _selectedCustomer.IsActive = !_selectedCustomer.IsActive;
        _customers.UpdateCustomer(_selectedCustomer);
        ShowDetail();
    }

    // ----- Sale history drill-down: Series -> Issues -> Line items -----

    private void ShowHistorySeries()
    {
        _screen = Screen.HistorySeries;

        btnBack.Visible = true;
        txtSearch.Visible = true;
        addPanel.Visible = false;
        pullListAddPanel.Visible = false;
        btnVoidSale.Visible = false;
        grid.Visible = true;
        detailPanel.Visible = false;

        grid.DataSource = null;
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series", ReadOnly = true });

        var searchText = txtSearch.Text;

        var seriesNames = _customerHistory
            .Select(l => l.Cover.Issue.Series)
            .DistinctBy(s => s.Name)
            .Where(s => string.IsNullOrWhiteSpace(searchText) ||
                        s.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name)
            .ToList();

        foreach (var series in seriesNames)
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Series"].Value = $"{series.Name} ({series.StartYear})";
            row.Tag = series.Name;
        }
    }

    private void ShowHistoryIssues()
    {
        if (_selectedSeriesName == null) { ShowHistorySeries(); return; }

        _screen = Screen.HistoryIssues;

        btnBack.Visible = true;
        txtSearch.Visible = false;
        addPanel.Visible = false;
        pullListAddPanel.Visible = false;
        btnVoidSale.Visible = false;
        grid.Visible = true;
        detailPanel.Visible = false;

        grid.DataSource = null;
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Issue", HeaderText = "Issue #", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TimesBought", HeaderText = "Times Bought", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LastBought", HeaderText = "Last Bought", ReadOnly = true });

        var issueGroups = _customerHistory
            .Where(l => l.Cover.Issue.Series.Name == _selectedSeriesName)
            .GroupBy(l => l.Cover.Issue.IssueNumber)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in issueGroups)
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Issue"].Value = group.Key.ToString("0.##");
            row.Cells["TimesBought"].Value = group.Sum(l => l.Quantity);
            row.Cells["LastBought"].Value = group.Max(l => l.Sale.SoldAt).ToString("d");
            row.Tag = group.Key;
        }
    }

    private void ShowHistoryLines()
    {
        if (_selectedSeriesName == null || _selectedIssueNumber == null) { ShowHistoryIssues(); return; }

        _screen = Screen.HistoryLines;

        btnBack.Visible = true;
        txtSearch.Visible = false;
        addPanel.Visible = false;
        pullListAddPanel.Visible = false;
        btnVoidSale.Visible = true;
        grid.Visible = true;
        detailPanel.Visible = false;

        grid.DataSource = null;
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cover", HeaderText = "Cover", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Quantity", HeaderText = "Quantity", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PriceEach", HeaderText = "Price Each", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Discount", HeaderText = "Discount %", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SoldAt", HeaderText = "Date", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", ReadOnly = true });

        var lines = _customerHistory
            .Where(l => l.Cover.Issue.Series.Name == _selectedSeriesName && l.Cover.Issue.IssueNumber == _selectedIssueNumber)
            .OrderByDescending(l => l.Sale.SoldAt)
            .ToList();

        foreach (var line in lines)
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Cover"].Value = line.Cover.CoverLetter;
            row.Cells["Quantity"].Value = line.Quantity;
            row.Cells["PriceEach"].Value = line.PriceEach.ToString("0.00");
            row.Cells["Discount"].Value = line.DiscountPercent.ToString("0.00");
            row.Cells["SoldAt"].Value = line.Sale.SoldAt.ToString("g");
            row.Cells["Status"].Value = line.Sale.IsVoided ? "VOIDED" : "";
            if (line.Sale.IsVoided)
                row.DefaultCellStyle.ForeColor = Color.Gray;
            row.Tag = line;
        }
    }

    private void BtnVoidSale_Click(object? sender, EventArgs e)
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

        // Refresh the in-memory history snapshot too, so the Status column updates.
        if (_selectedCustomer != null)
            _customerHistory = _history.GetHistoryForCustomer(_selectedCustomer.Id);

        ShowHistoryLines();
    }

    // ----- Pull list screen -----

    private void ShowPullList()
    {
        if (_selectedCustomer == null) return;

        _screen = Screen.PullList;

        btnBack.Visible = true;
        txtSearch.Visible = false;
        addPanel.Visible = false;
        pullListAddPanel.Visible = true;
        btnVoidSale.Visible = false;
        grid.Visible = true;
        detailPanel.Visible = false;
        lblPullListHeader.Visible = true;
        lblPullListHeader.Text = $"{_selectedCustomer.Name} — Pull List";

        txtPullSeriesSearch.Clear();
        lstPullSeriesResults.Items.Clear();
        _pullSeriesResults.Clear();
        _selectedSeriesForPullList = null;

        RefreshPullListGrid();
    }

    private void RefreshPullListGrid()
    {
        if (_selectedCustomer == null) return;

        grid.DataSource = null;
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cover", HeaderText = "Preferred Cover", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Qty", HeaderText = "Qty", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Notes", HeaderText = "Notes", ReadOnly = true });

        var entries = _pullLists.GetForCustomer(_selectedCustomer.Id);

        foreach (var entry in entries)
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Series"].Value = $"{entry.Series.Name} ({entry.Series.StartYear})";
            row.Cells["Cover"].Value = entry.PreferredCoverLetter;
            row.Cells["Qty"].Value = entry.Quantity;
            row.Cells["Notes"].Value = entry.Notes ?? "";
            row.Tag = entry;
        }
    }

    private void EditPullListEntry(int entryId, int seriesId, string seriesLabel, string currentCover, object? qtyCellValue, string? currentNotes)
    {
        var currentQty = 1;
        if (qtyCellValue != null) int.TryParse(qtyCellValue.ToString(), out currentQty);
        if (currentQty < 1) currentQty = 1;

        var knownCovers = _inventory.GetKnownCoverLetters(seriesId);
        using var form = new PullListEntryForm(seriesLabel, currentCover, currentQty, currentNotes, isEditingExisting: true, knownCoverLetters: knownCovers);
        var result = form.ShowDialog(this);

        if (result == DialogResult.Abort) // "Remove from Pull List" inside the dialog
        {
            _pullLists.RemoveFromPullList(entryId);
            RefreshPullListGrid();
            return;
        }

        if (result != DialogResult.OK) return;

        _pullLists.UpdatePullListEntry(entryId, form.CoverLetter, form.Quantity, form.Notes);
        RefreshPullListGrid();
    }

    private void PullSeriesSearch_TextChanged(object? sender, EventArgs e)
    {
        var text = txtPullSeriesSearch.Text;

        _pullSeriesResults = string.IsNullOrWhiteSpace(text)
            ? _inventory.GetAllSeriesForDisplay()
            : _inventory.SearchSeriesForDisplay(text);

        lstPullSeriesResults.Items.Clear();
        foreach (var series in _pullSeriesResults)
            lstPullSeriesResults.Items.Add($"{series.Name} ({series.StartYear})");

        _selectedSeriesForPullList = null;
    }

    private void BtnAddToPullList_Click(object? sender, EventArgs e)
    {
        if (_selectedCustomer == null) return;

        if (_selectedSeriesForPullList == null)
        {
            MessageBox.Show("Pick a series from the search results first.", "No Series Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var seriesLabel = $"{_selectedSeriesForPullList.Name} ({_selectedSeriesForPullList.StartYear})";
        var knownCovers = _inventory.GetKnownCoverLetters(_selectedSeriesForPullList.Id);
        using var form = new PullListEntryForm(seriesLabel, knownCovers.Count > 0 ? knownCovers[0] : "A", 1, null, isEditingExisting: false, knownCoverLetters: knownCovers);
        if (form.ShowDialog(this) != DialogResult.OK) return;

        _pullLists.AddToPullList(_selectedCustomer.Id, _selectedSeriesForPullList.Id, form.CoverLetter, form.Quantity, form.Notes);

        txtPullSeriesSearch.Clear();
        lstPullSeriesResults.Items.Clear();
        _selectedSeriesForPullList = null;

        RefreshPullListGrid();
    }

    // ----- Navigation -----

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var row = grid.Rows[e.RowIndex];

        switch (_screen)
        {
            case Screen.List:
                var idValue = row.Cells["Id"].Value;
                if (idValue == null) return;
                _selectedCustomer = _customers.FindById(Convert.ToInt32(idValue));
                if (_selectedCustomer != null) ShowDetail();
                break;

            case Screen.HistorySeries:
                if (row.Tag is string seriesName)
                {
                    _selectedSeriesName = seriesName;
                    ShowHistoryIssues();
                }
                break;

            case Screen.HistoryIssues:
                if (row.Tag is float issueNumber)
                {
                    _selectedIssueNumber = issueNumber;
                    ShowHistoryLines();
                }
                break;

            case Screen.PullList:
                if (row.Tag is PullListEntry pullEntry)
                {
                    EditPullListEntry(pullEntry.Id, pullEntry.SeriesId, row.Cells["Series"].Value?.ToString() ?? "", row.Cells["Cover"].Value?.ToString() ?? "A", row.Cells["Qty"].Value, row.Cells["Notes"].Value?.ToString());
                }
                break;
        }
    }

    private void BtnBack_Click(object? sender, EventArgs e)
    {
        switch (_screen)
        {
            case Screen.Detail:
                ShowList();
                break;
            case Screen.Edit:
                ShowDetail();
                break;
            case Screen.HistorySeries:
                ShowDetail();
                break;
            case Screen.HistoryIssues:
                _selectedSeriesName = null;
                ShowHistorySeries();
                break;
            case Screen.HistoryLines:
                _selectedIssueNumber = null;
                ShowHistoryIssues();
                break;
            case Screen.PullList:
                ShowDetail();
                break;
            default:
                ShowList();
                break;
        }
    }

    // Re-clicking the Customers tab while already on it (MainForm's
    // tab strip) jumps back out to the customer list instead of leaving
    // you stuck wherever you'd drilled into (detail, history, pull list).
    public void ResetToTop() => ShowList();
}
