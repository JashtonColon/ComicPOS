using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

// Modal "Add New Item" dialog, reached from the Inventory tab's series list.
//
// Scan/type a barcode first: if it matches something already in the system
// (most commonly a New Books placeholder created by the weekly Comic Vine
// check - series, issue #, publisher, cover art and dates already filled
// in, just waiting to be priced and stocked), every field below fills in
// automatically from that local record - no new Comic Vine API call, no
// retyping what's already known. This is how a shipment gets "received":
// scan the book, set price and quantity, save. A barcode with no match
// falls through to plain manual entry, same as before.
public class AddItemForm : Form
{
    private readonly IInventoryService _inventory;

    private readonly TextBox txtBarcode = new();
    private readonly Button btnLookUp = new();
    private readonly Label lblLookupStatus = new();
    private readonly PictureBox picCover = new();

    private readonly TextBox txtSeriesName = new();
    private readonly TextBox txtStartYear = new();
    private readonly TextBox txtIssueNumber = new();
    private readonly ComboBox cboType = new();
    private readonly TextBox txtPublisher = new();
    private readonly TextBox txtCoverLetter = new() { Text = "A" };
    private readonly TextBox txtPrice = new();
    private readonly TextBox txtStock = new();
    private readonly TextBox txtDescription = new();
    private readonly DateTimePicker dtFoc = new();
    private readonly CheckBox chkHasFoc = new();
    private readonly DateTimePicker dtRelease = new();
    private readonly CheckBox chkHasRelease = new();
    private readonly Button btnSave = new();
    private readonly Button btnCancel = new();
    private readonly Label lblError = new();

    // Set once a barcode lookup finds an existing Cover, so Save knows
    // this is "receive an existing item" rather than "create a new one" -
    // not that it changes what gets called (AddOrUpdateItem handles both
    // the same way, matched by barcode), just what the status text says.
    private bool _matchedExisting;

    public AddItemForm(IInventoryService inventory)
    {
        _inventory = inventory;

        Text = "Add New Item";
        Width = 460;
        Height = 660;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        // --- Barcode lookup strip, at the top so it's the first thing used ---
        var scanPanel = new Panel { Dock = DockStyle.Top, Height = 90, Padding = new Padding(12, 10, 12, 0) };

        var lblBarcode = new Label { Text = "Scan or type barcode:", Dock = DockStyle.Top, Height = 20, TextAlign = ContentAlignment.MiddleLeft, Font = UiTheme.BoldFont };

        var barcodeRow = new Panel { Dock = DockStyle.Top, Height = 32 };
        btnLookUp.Text = "Look Up";
        btnLookUp.Dock = DockStyle.Right;
        btnLookUp.Width = 90;
        btnLookUp.Click += (s, e) => LookUpBarcode();
        txtBarcode.Dock = DockStyle.Fill;
        txtBarcode.KeyDown += (s, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true; // a barcode scanner "types" the code then Enter - treat it as Look Up, not Save
            LookUpBarcode();
        };
        // Dock=Right stacking: last added ends up closest to the right edge.
        barcodeRow.Controls.Add(txtBarcode);
        barcodeRow.Controls.Add(btnLookUp);

        lblLookupStatus.Dock = DockStyle.Top;
        lblLookupStatus.Height = 28;
        lblLookupStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblLookupStatus.ForeColor = UiTheme.TextMuted;
        lblLookupStatus.Text = "Scan a barcode already in New Books to auto-fill everything below, or just fill the form in by hand.";

        // Dock=Top stacking: last added ends up closest to the top.
        scanPanel.Controls.Add(lblLookupStatus);
        scanPanel.Controls.Add(barcodeRow);
        scanPanel.Controls.Add(lblBarcode);

        // --- Cover thumbnail, shown only once a match brings one in ---
        picCover.Dock = DockStyle.Right;
        picCover.Width = 110;
        picCover.SizeMode = PictureBoxSizeMode.Zoom;
        picCover.Visible = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control input)
        {
            layout.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, layout.RowStyles.Count);
            input.Dock = DockStyle.Fill;
            layout.Controls.Add(input, 1, layout.RowStyles.Count);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        }

        void AddRowWithCheck(string label, CheckBox check, DateTimePicker picker)
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            check.Text = "Set date";
            check.Dock = DockStyle.Left;
            check.Width = 80;
            picker.Dock = DockStyle.Fill;
            picker.Enabled = false;
            check.CheckedChanged += (s, e) => picker.Enabled = check.Checked;
            panel.Controls.Add(picker);
            panel.Controls.Add(check);
            AddRow(label, panel);
        }

        cboType.DropDownStyle = ComboBoxStyle.DropDownList;
        cboType.Items.Add("Single Issue");
        cboType.Items.Add("Collected Edition");
        cboType.SelectedIndex = 0;

        txtStartYear.Text = DateTime.Now.Year.ToString();
        txtIssueNumber.Text = "1";
        txtPrice.Text = "0.00";
        txtStock.Text = "0";

        AddRow("Series Name:*", txtSeriesName);
        AddRow("Series Start Year:*", txtStartYear);
        AddRow("Issue Number:*", txtIssueNumber);
        AddRow("Type:", cboType);
        AddRow("Publisher:", txtPublisher);
        AddRow("Cover Letter:", txtCoverLetter);
        AddRow("Price:*", txtPrice);
        AddRow("Starting Stock Qty:*", txtStock);
        AddRow("Description:", txtDescription);
        AddRowWithCheck("FOC Date:", chkHasFoc, dtFoc);
        AddRowWithCheck("Release Date:", chkHasRelease, dtRelease);

        var formBody = new Panel { Dock = DockStyle.Fill };
        formBody.Controls.Add(layout);
        formBody.Controls.Add(picCover);

        lblError.Dock = DockStyle.Top;
        lblError.Height = 36;
        lblError.ForeColor = Color.Red;
        lblError.TextAlign = ContentAlignment.MiddleLeft;

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        btnCancel.Text = "Cancel";
        btnCancel.Width = 90;
        btnCancel.DialogResult = DialogResult.Cancel;
        btnSave.Text = "Save Item";
        btnSave.Width = 110;
        btnSave.Click += BtnSave_Click;
        btnSave.Tag = "primary";
        buttonPanel.Controls.Add(btnCancel);
        buttonPanel.Controls.Add(btnSave);

        // Dock=Top stacking: last added ends up closest to the top, so add
        // in reverse of reading order (buttons pinned above the error line,
        // which sits above the body, below the scan strip which is already
        // docked Top and added first).
        Controls.Add(formBody);
        Controls.Add(buttonPanel);
        Controls.Add(lblError);
        Controls.Add(scanPanel);

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);

        txtBarcode.Focus();
    }

    private void LookUpBarcode()
    {
        lblError.Text = "";
        var barcode = txtBarcode.Text.Trim();
        if (string.IsNullOrWhiteSpace(barcode))
        {
            lblLookupStatus.Text = "Type or scan a barcode first.";
            lblLookupStatus.ForeColor = UiTheme.TextMuted;
            return;
        }

        var cover = _inventory.FindByBarcode(barcode);
        if (cover == null)
        {
            _matchedExisting = false;
            picCover.Visible = false;
            lblLookupStatus.Text = $"No existing item for \"{barcode}\" - fill in the details below to add it as new.";
            lblLookupStatus.ForeColor = UiTheme.TextMuted;
            return;
        }

        _matchedExisting = true;
        var issue = cover.Issue;
        var series = issue.Series;

        txtSeriesName.Text = series.Name;
        txtStartYear.Text = series.StartYear.ToString();
        txtIssueNumber.Text = issue.IssueNumber.ToString("0.##");
        cboType.SelectedIndex = issue.Type == IssueType.CollectedEdition ? 1 : 0;
        txtPublisher.Text = issue.Publisher;
        txtCoverLetter.Text = string.IsNullOrWhiteSpace(cover.CoverLetter) ? "A" : cover.CoverLetter;
        txtPrice.Text = cover.Price.ToString("0.00");
        txtStock.Text = cover.StockQuantity.ToString();
        txtDescription.Text = cover.Description ?? "";

        chkHasFoc.Checked = issue.FocDate != null;
        if (issue.FocDate != null) dtFoc.Value = issue.FocDate.Value.ToDateTime(TimeOnly.MinValue);
        chkHasRelease.Checked = issue.ReleaseDate != null;
        if (issue.ReleaseDate != null) dtRelease.Value = issue.ReleaseDate.Value.ToDateTime(TimeOnly.MinValue);

        if (!string.IsNullOrWhiteSpace(cover.CoverImagePath) && File.Exists(cover.CoverImagePath))
        {
            try
            {
                using var fs = new FileStream(cover.CoverImagePath, FileMode.Open, FileAccess.Read);
                picCover.Image = Image.FromStream(fs);
                picCover.Visible = true;
            }
            catch
            {
                picCover.Visible = false;
            }
        }
        else
        {
            picCover.Visible = false;
        }

        lblLookupStatus.Text = $"Found: {series.Name} #{issue.IssueNumber:0.##} - review price/stock below and save to add it to inventory.";
        lblLookupStatus.ForeColor = UiTheme.PrimaryAccent;
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        lblError.Text = "";

        if (string.IsNullOrWhiteSpace(txtSeriesName.Text))
        {
            lblError.Text = "Series Name is required.";
            return;
        }
        if (!int.TryParse(txtStartYear.Text, out var startYear) || startYear < 1900)
        {
            lblError.Text = "Series Start Year must be a valid year.";
            return;
        }
        if (!float.TryParse(txtIssueNumber.Text, out var issueNumber))
        {
            lblError.Text = "Issue Number must be a number (e.g. 1 or 1.5).";
            return;
        }
        if (string.IsNullOrWhiteSpace(txtBarcode.Text))
        {
            lblError.Text = "Barcode / UPC is required - scan or type one at the top.";
            return;
        }
        if (!decimal.TryParse(txtPrice.Text, out var price) || price < 0)
        {
            lblError.Text = "Price must be a non-negative number.";
            return;
        }
        if (!int.TryParse(txtStock.Text, out var stock) || stock < 0)
        {
            lblError.Text = "Starting Stock Qty must be a non-negative whole number.";
            return;
        }

        var type = cboType.SelectedIndex == 1 ? IssueType.CollectedEdition : IssueType.SingleIssue;
        var coverLetter = string.IsNullOrWhiteSpace(txtCoverLetter.Text) ? "A" : txtCoverLetter.Text.Trim();
        var description = string.IsNullOrWhiteSpace(txtDescription.Text) ? null : txtDescription.Text.Trim();
        DateOnly? focDate = chkHasFoc.Checked ? DateOnly.FromDateTime(dtFoc.Value) : null;
        DateOnly? releaseDate = chkHasRelease.Checked ? DateOnly.FromDateTime(dtRelease.Value) : null;

        try
        {
            _inventory.AddOrUpdateItem(
                txtSeriesName.Text.Trim(),
                startYear,
                issueNumber,
                type,
                txtPublisher.Text.Trim(),
                focDate,
                releaseDate,
                coverLetter,
                txtBarcode.Text.Trim(),
                price,
                stock,
                description);

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }
}
