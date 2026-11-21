namespace ComicPos.App;

// Modal dialog for adding or editing one customer's pull-list subscription
// to a series: which single cover they want, how many copies, and any
// per-subscription note. Used both for a brand-new entry (from Customer ->
// Pull List -> pick a series -> Add) and for editing one already on the
// list (double-click a row) - same validation either way, instead of the
// old flow where the only way to "fix" a wrong cover was delete-then-
// re-add, which lost the row's Id and any history tied to it.
//
// When opened for an existing entry, a "Remove from Pull List" button is
// shown too; picking it confirms once, then closes with DialogResult.Abort
// so the caller can tell "removed" apart from "saved" (OK) and "cancelled".
public class PullListEntryForm : Form
{
    private readonly ComboBox cboCover = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox txtQuantity = new() { Text = "1" };
    private readonly TextBox txtNotes = new() { Multiline = true, Height = 70 };
    private readonly Button btnSave = new();
    private readonly Button btnCancel = new();
    private readonly Button btnRemove = new();
    private readonly Label lblError = new();

    public string CoverLetter { get; private set; } = "A";
    public int Quantity { get; private set; } = 1;
    public string? Notes { get; private set; }

    public PullListEntryForm(string seriesLabel, string initialCover, int initialQuantity, string? initialNotes, bool isEditingExisting, List<string>? knownCoverLetters = null)
    {
        Text = isEditingExisting ? "Edit Pull List Entry" : "Add to Pull List";
        Width = 400;
        // A bit taller than before - the footer button row(s) grew to fit
        // the now-taller, rounded buttons (see UiTheme.StyleButton).
        Height = isEditingExisting ? 380 : 330;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        // Title at the absolute top (Dock=Top, the only Top-docked
        // control here), the field layout filling everything in between
        // (Dock=Fill - no competing-for-space ambiguity with anything
        // else), and the error line + Save/Cancel anchored to Dock=Bottom
        // - so Save/Cancel can never end up stacked ABOVE the series name
        // and the fields the way they used to (every control here was
        // Dock=Top before, and which one landed closest to the window's
        // actual top edge turned out to depend on add order in a way
        // that put the button bar first).
        var lblSeries = new Label
        {
            Text = seriesLabel,
            Dock = DockStyle.Top,
            // AutoSize (not a fixed Height) - a hardcoded pixel height
            // guessed against this font's rendered size, and clipped the
            // top of the text once real DPI scaling and the Padding
            // below actually got applied.
            AutoSize = true,
            Font = UiTheme.HeaderFont,
            ForeColor = UiTheme.PrimaryDark,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 10, 12, 0)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12, 10, 12, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control input, int height)
        {
            layout.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, layout.RowStyles.Count);
            input.Dock = DockStyle.Fill;
            layout.Controls.Add(input, 1, layout.RowStyles.Count);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        }

        // Real cover letters this store has actually seen for this series
        // (from whatever issues/covers are already on file) instead of a
        // blind free-text box with no idea what the series even comes in
        // - still editable (DropDown, not DropDownList) since a brand
        // new cover variant can always show up on a future issue that
        // hasn't been seen yet.
        foreach (var letter in knownCoverLetters ?? new List<string>())
            cboCover.Items.Add(letter);
        cboCover.Text = initialCover;
        cboCover.MaxLength = 1;

        txtQuantity.Text = initialQuantity.ToString();
        txtNotes.Text = initialNotes ?? "";

        AddRow("Cover:*", cboCover, 30);
        AddRow("Quantity:*", txtQuantity, 30);
        AddRow("Notes:", txtNotes, 74);

        lblError.Dock = DockStyle.Bottom;
        lblError.Height = 32;
        lblError.ForeColor = Color.Red;
        lblError.TextAlign = ContentAlignment.MiddleLeft;
        lblError.Padding = new Padding(12, 0, 12, 0);

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        btnCancel.Text = "Cancel";
        btnCancel.Width = 90;
        btnCancel.DialogResult = DialogResult.Cancel;
        btnSave.Text = "Save";
        btnSave.Width = 90;
        btnSave.Click += BtnSave_Click;
        btnSave.Tag = "primary";
        buttonPanel.Controls.Add(btnCancel);
        buttonPanel.Controls.Add(btnSave);

        if (isEditingExisting)
        {
            var removePanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10) };
            btnRemove.Text = "Remove from Pull List";
            btnRemove.AutoSize = true;
            btnRemove.ForeColor = Color.Firebrick;
            btnRemove.Click += BtnRemove_Click;
            removePanel.Controls.Add(btnRemove);
            Controls.Add(removePanel);
        }

        Controls.Add(layout);
        Controls.Add(buttonPanel);
        Controls.Add(lblError);
        Controls.Add(lblSeries);

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);

        cboCover.Focus();
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        lblError.Text = "";

        var cover = cboCover.Text.Trim().ToUpperInvariant();
        if (cover.Length != 1 || !char.IsLetter(cover[0]))
        {
            lblError.Text = "Cover letter must be a single letter (A, B, C, ...).";
            return;
        }

        if (!int.TryParse(txtQuantity.Text.Trim(), out var qty) || qty < 1)
        {
            lblError.Text = "Quantity must be a whole number, 1 or more.";
            return;
        }

        CoverLetter = cover;
        Quantity = qty;
        Notes = string.IsNullOrWhiteSpace(txtNotes.Text) ? null : txtNotes.Text.Trim();

        DialogResult = DialogResult.OK;
        Close();
    }

    private void BtnRemove_Click(object? sender, EventArgs e)
    {
        var confirm = MessageBox.Show(
            "Remove this series from the customer's pull list?",
            "Remove",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        DialogResult = DialogResult.Abort; // distinct from OK/Cancel - caller treats this as "remove"
        Close();
    }
}
