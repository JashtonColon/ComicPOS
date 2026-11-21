using System.Globalization;
using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

// "New Books" tab: a browsable list of everything releasing in roughly the
// last 3 months through the next 3 months, with cover art. Fully automatic -
// no per-series linking step. A background weekly check (see MainForm)
// keeps this populated on its own; the button here is just a manual nudge.
public class AutoFetchControl : UserControl
{
    private readonly IAutoFetchService _autoFetch;

    private readonly Panel topPanel;
    private readonly Label lblStatus = new();
    private readonly ComboBox cboSortBy = new();
    private readonly Button btnCheckNow = new();
    private readonly Button btnRescan = new();
    private readonly TextBox txtSearch = new();
    private readonly FlowLayoutPanel monthTabsPanel = new();
    private readonly DataGridView grid = new();

    // null = "All" - the inline scrolling view with group headers, same as
    // before month tabs existed. Set when a specific month tab is clicked.
    private string? _selectedMonthLabel;
    private List<string> _lastMonthTabLabels = new();

    // Built once and reused for every row with no real cover art on file -
    // RefreshGrid runs on every search-box keystroke, so regenerating this
    // small bitmap per row per keystroke would be wasteful for no visual
    // difference (it's the same tile every time either way).
    private static readonly Image NoCoverThumbnail = UiTheme.BuildNoCoverPlaceholder(44, 60);

    private const string SortTitle = "Title";
    private const string SortPublisher = "Publisher";
    private const string SortFoc = "FOC Date";
    private const string SortRelease = "Release Date";

    public AutoFetchControl(IAutoFetchService autoFetch)
    {
        _autoFetch = autoFetch;

        Dock = DockStyle.Fill;

        topPanel = new Panel { Dock = DockStyle.Top, Height = 40 };

        btnCheckNow.Dock = DockStyle.Right;
        btnCheckNow.Width = 150;
        btnCheckNow.Text = "Check Now";
        btnCheckNow.Tag = "primary";
        btnCheckNow.Click += (s, e) => RunCheck(showResultPopup: true);

        // A manual, explicit "fix my existing data" action - re-verifies
        // every already-linked series' Comic Vine volume link and
        // backfills missing cover art for every issue on file, ignoring
        // the normal recheck cooldown. Separate from "Check Now" (which
        // only looks for brand-new issues/series) because this is a much
        // heavier, occasional-use operation, not something to run on
        // every click or every weekly background check.
        btnRescan.Dock = DockStyle.Right;
        btnRescan.Width = 160;
        btnRescan.Text = "Re-scan Existing";
        btnRescan.Click += (s, e) => RunRescan();

        cboSortBy.Dock = DockStyle.Right;
        cboSortBy.Width = 140;
        cboSortBy.DropDownStyle = ComboBoxStyle.DropDownList;
        cboSortBy.Items.Add(SortRelease);
        cboSortBy.Items.Add(SortFoc);
        cboSortBy.Items.Add(SortPublisher);
        cboSortBy.Items.Add(SortTitle);
        cboSortBy.SelectedIndex = 0;
        cboSortBy.SelectedIndexChanged += (s, e) =>
        {
            // A month label from one sort (e.g. a FOC month) has no
            // meaning under another (e.g. sorted by Publisher) - drop
            // back to "All" instead of silently filtering to a month
            // group that no longer means what the tab said it did.
            _selectedMonthLabel = null;
            RefreshGrid();
        };

        var lblSortBy = new Label { Dock = DockStyle.Right, Width = 60, TextAlign = ContentAlignment.MiddleRight, Text = "Sort by:" };

        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.ForeColor = UiTheme.TextMuted;

        // Dock=Right stacking: last added ends up closest to the right edge.
        topPanel.Controls.Add(lblStatus);
        topPanel.Controls.Add(lblSortBy);
        topPanel.Controls.Add(cboSortBy);
        topPanel.Controls.Add(btnCheckNow);
        topPanel.Controls.Add(btnRescan);

        txtSearch.Dock = DockStyle.Top;
        txtSearch.PlaceholderText = "Search New Books by series or publisher...";
        txtSearch.TextChanged += (s, e) => RefreshGrid();

        monthTabsPanel.Dock = DockStyle.Top;
        monthTabsPanel.AutoSize = true;
        monthTabsPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        monthTabsPanel.FlowDirection = FlowDirection.LeftToRight;
        monthTabsPanel.WrapContents = true;
        monthTabsPanel.Padding = new Padding(4, 4, 4, 0);

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.RowTemplate.Height = 64;
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewImageColumn { Name = "Cover", HeaderText = "", Width = 48, ImageLayout = DataGridViewImageCellLayout.Zoom });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Issue", HeaderText = "Issue #", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Publisher", HeaderText = "Publisher", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Foc", HeaderText = "FOC Date", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Release", HeaderText = "Release Date", ReadOnly = true });
        grid.CellDoubleClick += Grid_CellDoubleClick;

        Controls.Add(grid);
        Controls.Add(monthTabsPanel);
        Controls.Add(txtSearch);
        Controls.Add(topPanel);

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
        grid.RowTemplate.Height = 64; // StyleAll's default row height is too short for cover thumbnails - re-apply after it runs

        // StyleAll's default AutoSizeColumnsMode is DisplayedCells, which
        // recalculates column widths from whatever rows are currently
        // visible on every scroll. Combined with this grid's mixed row
        // heights (64px cover rows next to thin group-header rows) and
        // hundreds of New Books rows, that recalculation was fighting the
        // scrollbar and snapping it back toward the top mid-scroll. Fill
        // sizes columns once (on resize, not on every scroll) and leaves
        // the picture column alone at a fixed width.
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns["Cover"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        grid.Columns["Cover"]!.Width = 48;
        grid.Columns["Series"]!.FillWeight = 40;
        grid.Columns["Issue"]!.FillWeight = 10;
        grid.Columns["Publisher"]!.FillWeight = 20;
        grid.Columns["Foc"]!.FillWeight = 15;
        grid.Columns["Release"]!.FillWeight = 15;

        RefreshGrid();

        // Deferred to Load (not run here in the constructor): RunCheck
        // disables the owner Form while it works, found via FindForm() -
        // but this control isn't parented to a Form yet during its own
        // constructor, so that safety check would silently find nothing
        // to disable. By Load, MainForm has already added this control to
        // its tab, so FindForm() correctly resolves.
        Load += (s, e) => RunWeeklyCheckIfDue();
    }

    // This can mean dozens of sequential HTTP calls to Comic Vine (one per
    // newly-discovered series, to look up its publisher - see the cap in
    // AutoFetchService), which can take a while. Running it directly on
    // the UI thread used to freeze the whole app - you couldn't even switch
    // tabs - until every call finished. CheckForNewIssues() itself is still
    // synchronous (plain blocking HttpClient calls), so it's pushed onto a
    // background thread here and the result is marshaled back to the UI
    // thread instead. The rest of the app (other tabs, checkout, etc.) stays
    // responsive while this runs.
    private bool _checkRunning;

    private void RunCheck(bool showResultPopup)
    {
        if (_checkRunning) return; // already running - ignore a second click
        _checkRunning = true;

        btnCheckNow.Enabled = false;
        btnRescan.Enabled = false; // shares the one DB connection with RunCheck - never let both run at once
        lblStatus.Text = "Checking for new issues... this can take a minute.";

        // The shared database connection isn't safe for two threads to use
        // at once, so while the check runs on a background thread, the rest
        // of the app's INPUT is disabled (nothing else can touch the
        // database meanwhile) - but the window itself stays responsive at
        // the OS level: it can still be moved, minimized, or closed, and
        // Windows won't mark it "Not Responding" the way a blocked UI
        // thread used to.
        var ownerForm = FindForm();
        if (ownerForm != null) ownerForm.Enabled = false;
        Cursor.Current = Cursors.WaitCursor;

        Task.Run(() =>
        {
            AutoFetchResult? result = null;
            Exception? error = null;
            try
            {
                result = _autoFetch.CheckForNewIssues();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (IsDisposed) return; // tab/control closed while the check was running
            BeginInvoke(() => FinishCheck(result, error, showResultPopup, ownerForm));
        });
    }

    private void FinishCheck(AutoFetchResult? result, Exception? error, bool showResultPopup, Form? ownerForm)
    {
        _checkRunning = false;
        btnCheckNow.Enabled = true;
        btnRescan.Enabled = true;
        if (ownerForm != null) ownerForm.Enabled = true;
        Cursor.Current = Cursors.Default;

        if (error != null)
        {
            UpdateStatusLabel();
            if (showResultPopup)
                MessageBox.Show($"Check failed: {error.Message}", "New Books Check", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        RefreshGrid(); // also calls UpdateStatusLabel()

        if (showResultPopup && result != null)
        {
            var summary = $"Checked {result.SeriesChecked} series, linked {result.SeriesAutoLinked} new one(s), added {result.NewIssuesAdded} new issue(s).";
            if (result.Errors.Count > 0)
                summary += "\n\nSome series had errors:\n" + string.Join("\n", result.Errors.Take(10));
            MessageBox.Show(summary, "New Books Check", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // A manual, explicit "fix my existing data" action (see
    // IAutoFetchService.RescanExistingBooks) - mirrors RunCheck's
    // Task.Run/form-disable safety wrapper exactly, since this can also
    // mean a lot of sequential Comic Vine calls (one per already-linked
    // series) and would freeze the UI the same way if run directly on
    // the UI thread.
    private void RunRescan()
    {
        if (_checkRunning) return; // a check or another rescan is already running - ignore a second click
        _checkRunning = true;

        btnCheckNow.Enabled = false;
        btnRescan.Enabled = false;
        lblStatus.Text = "Re-scanning existing books for better volume links and missing covers... this can take a while for a large catalog.";

        var ownerForm = FindForm();
        if (ownerForm != null) ownerForm.Enabled = false;
        Cursor.Current = Cursors.WaitCursor;

        Task.Run(() =>
        {
            AutoFetchResult? result = null;
            Exception? error = null;
            try
            {
                result = _autoFetch.RescanExistingBooks();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (IsDisposed) return; // tab/control closed while the rescan was running
            BeginInvoke(() => FinishRescan(result, error, ownerForm));
        });
    }

    private void FinishRescan(AutoFetchResult? result, Exception? error, Form? ownerForm)
    {
        _checkRunning = false;
        btnCheckNow.Enabled = true;
        btnRescan.Enabled = true;
        if (ownerForm != null) ownerForm.Enabled = true;
        Cursor.Current = Cursors.Default;

        if (error != null)
        {
            UpdateStatusLabel();
            MessageBox.Show($"Re-scan failed: {error.Message}", "Re-scan Existing Books", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        RefreshGrid(); // also calls UpdateStatusLabel()

        if (result != null)
        {
            var summary = $"Checked {result.SeriesChecked} series, re-linked {result.SeriesAutoLinked}, backfilled {result.CoversBackfilled} cover(s).";
            if (result.Errors.Count > 0)
                summary += "\n\nSome series had errors:\n" + string.Join("\n", result.Errors.Take(10));
            MessageBox.Show(summary, "Re-scan Existing Books", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void UpdateStatusLabel()
    {
        var last = _autoFetch.LastCheckedAt();
        lblStatus.Text = last != null
            ? $"Last checked: {last:g}  •  checks automatically about once a week"
            : "Never checked yet  •  checks automatically about once a week";
    }

    // Called by MainForm when this tab is selected or a background weekly
    // check just ran, so the grid reflects whatever's newest without the
    // store having to click anything.
    public void RefreshAll() => RefreshGrid();

    // True while a background Check Now / weekly check is in flight. Other
    // parts of the app (MainForm's hourly timer, in particular) should
    // avoid starting their own DB work while this is true, since
    // ComicDbContext is a single shared instance and isn't thread-safe.
    public bool IsCheckRunning => _checkRunning;

    // Safe entry point for anything outside this control (MainForm's
    // hourly weekly-check timer) that wants to trigger the weekly Comic
    // Vine check. Routes through the same Task.Run/form-disable safety
    // wrapper as the "Check Now" button and the startup check - never call
    // _autoFetch.CheckForNewIssues() directly from outside this class, or
    // the UI-freeze bug this wrapper exists to prevent comes right back.
    public void RunWeeklyCheckIfDue()
    {
        if (_autoFetch.IsWeeklyCheckDue())
            RunCheck(showResultPopup: false);
    }

    private string GroupLabelFor(Issue issue, string sortKey) => sortKey switch
    {
        SortPublisher => string.IsNullOrWhiteSpace(issue.Publisher) ? "(Unknown Publisher)" : issue.Publisher,
        SortFoc => issue.FocDate?.ToString("MMMM yyyy") ?? "(No FOC Date)",
        SortTitle => issue.Series.Name,
        _ => issue.ReleaseDate?.ToString("MMMM yyyy") ?? "(No Release Date)"
    };

    private void RefreshGrid()
    {
        UpdateStatusLabel();

        IEnumerable<Issue> issues = _autoFetch.GetNewBooks();

        var searchText = txtSearch.Text.Trim();
        if (searchText.Length > 0)
        {
            issues = issues.Where(i =>
                i.Series.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(i.Publisher) && i.Publisher.Contains(searchText, StringComparison.OrdinalIgnoreCase)));
        }

        var sortKey = cboSortBy.SelectedItem as string ?? SortRelease;

        IEnumerable<Issue> ordered = sortKey switch
        {
            SortPublisher => issues.OrderBy(i => i.Publisher).ThenBy(i => i.Series.Name).ThenBy(i => i.IssueNumber),
            SortFoc => issues.OrderBy(i => i.FocDate ?? DateOnly.MaxValue).ThenBy(i => i.Series.Name),
            SortTitle => issues.OrderBy(i => i.Series.Name).ThenBy(i => i.IssueNumber),
            _ => issues.OrderBy(i => i.ReleaseDate ?? DateOnly.MaxValue).ThenBy(i => i.Series.Name)
        };

        var orderedList = ordered.ToList(); // enumerated once - reused below for the tabs and the rows

        // Clickable month tabs only make sense for the two date-based
        // sorts - a "month" isn't a meaningful grouping of Publisher or
        // Title, so the tab row just hides there and the grid falls back
        // to the plain scrolling list (no inline headers either, since
        // Title/Publisher already group inline the same way they always did).
        var monthCapableSort = sortKey == SortRelease || sortKey == SortFoc;
        monthTabsPanel.Visible = monthCapableSort;

        if (monthCapableSort)
        {
            var months = orderedList.Select(i => GroupLabelFor(i, sortKey)).Distinct().ToList();
            RebuildMonthTabs(months);

            if (_selectedMonthLabel != null)
                orderedList = orderedList.Where(i => GroupLabelFor(i, sortKey) == _selectedMonthLabel).ToList();
        }

        grid.Rows.Clear();
        string? lastGroupValue = null;

        foreach (var issue in orderedList)
        {
            var groupValue = GroupLabelFor(issue, sortKey);

            // Skip the inline group header once a single month tab has
            // already narrowed the list to just that month - repeating
            // the same label as a row above every single item would be
            // pure noise.
            var showInlineHeader = sortKey != SortTitle && !(monthCapableSort && _selectedMonthLabel != null);
            if (showInlineHeader && groupValue != lastGroupValue)
            {
                var headerIndex = grid.Rows.Add();
                var headerRow = grid.Rows[headerIndex];
                headerRow.Cells["Series"].Value = groupValue;
                headerRow.DefaultCellStyle.BackColor = UiTheme.RowAlt;
                headerRow.DefaultCellStyle.Font = UiTheme.BoldFont;
                headerRow.DefaultCellStyle.ForeColor = UiTheme.PrimaryDark;
                headerRow.ReadOnly = true;
                lastGroupValue = groupValue;
            }

            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Series"].Value = $"{issue.Series.Name} ({issue.Series.StartYear})";
            row.Cells["Issue"].Value = issue.IssueNumber.ToString("0.##");
            row.Cells["Publisher"].Value = string.IsNullOrWhiteSpace(issue.Publisher) ? "" : issue.Publisher;
            row.Cells["Foc"].Value = issue.FocDate?.ToString("MM/dd/yyyy") ?? "";
            row.Cells["Release"].Value = issue.ReleaseDate?.ToString("MM/dd/yyyy") ?? "";

            // The real Issue, not a re-parsed display string - double-click
            // (Grid_CellDoubleClick) opens the detail popup straight off
            // this, the same "row.Tag holds the real entity" pattern
            // Inventory's drill-down grids already use.
            row.Tag = issue;

            Image? coverImage = null;
            var coverWithImage = issue.Covers.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.CoverImagePath) && File.Exists(c.CoverImagePath));
            if (coverWithImage != null)
            {
                try
                {
                    using var fs = new FileStream(coverWithImage.CoverImagePath!, FileMode.Open, FileAccess.Read);
                    coverImage = Image.FromStream(fs);
                }
                catch
                {
                    // Missing/corrupt cached image - fall through to the placeholder below.
                }
            }

            // A plain "No Cover" tile instead of leaving the cell blank -
            // DataGridViewImageColumn draws its own broken-image red X for
            // a blank Image cell, which reads as a bug even when it just
            // means "Comic Vine doesn't catalog this one" (true for most
            // collected editions/TPs/HCs - there's no art to have here).
            row.Cells["Cover"].Value = coverImage ?? NoCoverThumbnail;
        }
    }

    private void RebuildMonthTabs(List<string> monthLabels)
    {
        // Chronological, not alphabetical - "February 2027" would
        // otherwise sort before "January 2027". Re-parsing "MMMM yyyy"
        // back into a date is the simplest way to get that order from
        // what's already just a display string.
        var chronological = monthLabels
            .OrderBy(label => DateTime.TryParseExact(label, "MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : DateTime.MaxValue)
            .ToList();

        // Only rebuild the actual button controls when the set of months
        // on offer changed (a fresh Check Now, or the search box narrowing
        // what's visible) - rebuilding on every click just to re-highlight
        // the selection would flicker and steal keyboard focus for no reason.
        if (!chronological.SequenceEqual(_lastMonthTabLabels))
        {
            monthTabsPanel.Controls.Clear();
            monthTabsPanel.Controls.Add(BuildMonthTabButton("All", null));
            foreach (var label in chronological)
                monthTabsPanel.Controls.Add(BuildMonthTabButton(label, label));
            _lastMonthTabLabels = chronological;

            // The previously-selected month might not be on offer anymore
            // (it emptied out, or the search box just filtered it away) -
            // fall back to "All" instead of silently showing zero rows.
            if (_selectedMonthLabel != null && !chronological.Contains(_selectedMonthLabel))
                _selectedMonthLabel = null;
        }

        foreach (Control c in monthTabsPanel.Controls)
            if (c is Button btn) HighlightMonthTabButton(btn);
    }

    private Button BuildMonthTabButton(string text, string? monthLabel)
    {
        var btn = new Button { Text = text, AutoSize = true, Margin = new Padding(4, 4, 0, 4), Tag = monthLabel };
        UiTheme.StyleButton(btn);
        btn.Click += (s, e) =>
        {
            _selectedMonthLabel = monthLabel;
            RefreshGrid();
        };
        return btn;
    }

    private void HighlightMonthTabButton(Button btn)
    {
        var isSelected = Equals(btn.Tag as string, _selectedMonthLabel);
        btn.BackColor = isSelected ? UiTheme.PrimaryAccent : UiTheme.NeutralButton;
        btn.ForeColor = isSelected ? Color.White : UiTheme.TextPrimary;
    }

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        if (grid.Rows[e.RowIndex].Tag is not Issue issue) return;

        using var form = new NewBookDetailForm(issue);
        form.ShowDialog(FindForm());
    }
}
