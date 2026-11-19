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
    private readonly DataGridView grid = new();

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

        cboSortBy.Dock = DockStyle.Right;
        cboSortBy.Width = 140;
        cboSortBy.DropDownStyle = ComboBoxStyle.DropDownList;
        cboSortBy.Items.Add(SortRelease);
        cboSortBy.Items.Add(SortFoc);
        cboSortBy.Items.Add(SortPublisher);
        cboSortBy.Items.Add(SortTitle);
        cboSortBy.SelectedIndex = 0;
        cboSortBy.SelectedIndexChanged += (s, e) => RefreshGrid();

        var lblSortBy = new Label { Dock = DockStyle.Right, Width = 60, TextAlign = ContentAlignment.MiddleRight, Text = "Sort by:" };

        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.ForeColor = UiTheme.TextMuted;

        // Dock=Right stacking: last added ends up closest to the right edge.
        topPanel.Controls.Add(lblStatus);
        topPanel.Controls.Add(lblSortBy);
        topPanel.Controls.Add(cboSortBy);
        topPanel.Controls.Add(btnCheckNow);

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

        Controls.Add(grid);
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

    private void RefreshGrid()
    {
        UpdateStatusLabel();

        var issues = _autoFetch.GetNewBooks();
        var sortKey = cboSortBy.SelectedItem as string ?? SortRelease;

        IEnumerable<Issue> ordered = sortKey switch
        {
            SortPublisher => issues.OrderBy(i => i.Publisher).ThenBy(i => i.Series.Name).ThenBy(i => i.IssueNumber),
            SortFoc => issues.OrderBy(i => i.FocDate ?? DateOnly.MaxValue).ThenBy(i => i.Series.Name),
            SortTitle => issues.OrderBy(i => i.Series.Name).ThenBy(i => i.IssueNumber),
            _ => issues.OrderBy(i => i.ReleaseDate ?? DateOnly.MaxValue).ThenBy(i => i.Series.Name)
        };

        grid.Rows.Clear();
        string? lastGroupValue = null;

        foreach (var issue in ordered)
        {
            var groupValue = sortKey switch
            {
                SortPublisher => string.IsNullOrWhiteSpace(issue.Publisher) ? "(Unknown Publisher)" : issue.Publisher,
                SortFoc => issue.FocDate?.ToString("MMMM yyyy") ?? "(No FOC Date)",
                SortTitle => issue.Series.Name,
                _ => issue.ReleaseDate?.ToString("MMMM yyyy") ?? "(No Release Date)"
            };

            if (sortKey != SortTitle && groupValue != lastGroupValue)
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

            var coverWithImage = issue.Covers.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.CoverImagePath) && File.Exists(c.CoverImagePath));
            if (coverWithImage != null)
            {
                try
                {
                    using var fs = new FileStream(coverWithImage.CoverImagePath!, FileMode.Open, FileAccess.Read);
                    row.Cells["Cover"].Value = Image.FromStream(fs);
                }
                catch
                {
                    // Missing/corrupt cached image - just show no thumbnail for this row.
                }
            }
        }
    }
}
