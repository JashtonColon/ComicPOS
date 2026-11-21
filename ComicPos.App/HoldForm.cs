using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

// Modal dialog for holding ONE specific issue/cover for a customer with
// no standing subscription attached - the "just this one" counterpart to
// PullListEntryForm's "every future issue of this series" subscription.
// Needs its own customer search (unlike PullListEntryForm, which is only
// ever opened from a screen that already has a customer selected) since
// this is launched from Inventory, which has no selected customer at all.
public class HoldForm : Form
{
    private readonly ICustomerService _customers;
    private List<Customer> _searchResults = new();
    private Customer? _selectedCustomer;

    private readonly TextBox txtCustomerSearch = new();
    private readonly ListBox lstCustomerResults = new();
    private readonly Panel searchPanel = new();
    private readonly Label lblSelectedCustomer = new();
    private readonly ComboBox cboCover = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox txtQuantity = new() { Text = "1" };
    private readonly TextBox txtNotes = new() { Multiline = true, Height = 60 };
    private readonly Button btnSave = new();
    private readonly Button btnCancel = new();
    private readonly Label lblError = new();

    public int CustomerId { get; private set; }
    public string CoverLetter { get; private set; } = "A";
    public int Quantity { get; private set; } = 1;
    public string? Notes { get; private set; }

    public HoldForm(ICustomerService customers, string issueLabel, List<string> knownCoverLetters, string defaultCoverLetter)
    {
        _customers = customers;

        Text = "Hold a Copy";
        Width = 420;
        // A bit taller than before - the footer below grew to fit the
        // now-taller, rounded buttons (see UiTheme.StyleButton).
        Height = 530;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        // Every section below is nested as Top+Fill pairs, never more
        // than one control sharing the same dock edge at any level -
        // PullListEntryForm used to stack several Dock=Top controls
        // directly and the Save/Cancel bar ended up rendering ABOVE the
        // series name instead of below the fields, so this form is built
        // to never depend on how multiple same-edge controls resolve
        // their own order against each other.
        var lblIssue = new Label
        {
            Text = issueLabel,
            Dock = DockStyle.Top,
            // AutoSize (not a fixed Height) - see PullListEntryForm's lblSeries
            // for why a hardcoded pixel height here clipped the text.
            AutoSize = true,
            Font = UiTheme.HeaderFont,
            ForeColor = UiTheme.PrimaryDark,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 10, 12, 0)
        };

        // 56 (buttonPanel) + 32 (lblError) - grown from 44+32 so the
        // taller, rounded Hold It/Cancel buttons (UiTheme.StyleButton) have
        // room without the search-result list overlap bug this was
        // originally mistaken for.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 88 };
        lblError.Dock = DockStyle.Top;
        lblError.Height = 32;
        lblError.ForeColor = Color.Red;
        lblError.TextAlign = ContentAlignment.MiddleLeft;
        lblError.Padding = new Padding(12, 0, 12, 0);

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        btnCancel.Text = "Cancel";
        btnCancel.Width = 90;
        btnCancel.DialogResult = DialogResult.Cancel;
        btnSave.Text = "Hold It";
        btnSave.Width = 90;
        btnSave.Click += BtnSave_Click;
        btnSave.Tag = "primary";
        buttonPanel.Controls.Add(btnCancel);
        buttonPanel.Controls.Add(btnSave);
        footer.Controls.Add(buttonPanel);
        footer.Controls.Add(lblError);

        var lblCustomerPrompt = new Label
        {
            Text = "Hold for which customer?",
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 4, 12, 0)
        };

        searchPanel.Dock = DockStyle.Top;
        searchPanel.Height = 140;
        searchPanel.Padding = new Padding(12, 0, 12, 0);
        txtCustomerSearch.Dock = DockStyle.Top;
        txtCustomerSearch.PlaceholderText = "Search customer by name...";
        txtCustomerSearch.TextChanged += TxtCustomerSearch_TextChanged;

        lstCustomerResults.Dock = DockStyle.Fill;
        lstCustomerResults.SelectedIndexChanged += (s, e) =>
        {
            _selectedCustomer = lstCustomerResults.SelectedIndex >= 0 && lstCustomerResults.SelectedIndex < _searchResults.Count
                ? _searchResults[lstCustomerResults.SelectedIndex]
                : null;
            UpdateSelectedCustomerLabel();
            UpdateSearchPanelState();
        };
        // Double-click fills the search box with the picked name, same
        // convention as the Pull List series search on the Customers tab
        // - "use this one" without re-triggering a fresh search against
        // the filled-in full name.
        lstCustomerResults.DoubleClick += (s, e) =>
        {
            if (lstCustomerResults.SelectedIndex < 0 || lstCustomerResults.SelectedIndex >= _searchResults.Count) return;
            var chosen = _searchResults[lstCustomerResults.SelectedIndex];

            txtCustomerSearch.TextChanged -= TxtCustomerSearch_TextChanged;
            txtCustomerSearch.Text = chosen.Name;
            txtCustomerSearch.SelectionStart = chosen.Name.Length;
            txtCustomerSearch.TextChanged += TxtCustomerSearch_TextChanged;

            _selectedCustomer = chosen;
            UpdateSelectedCustomerLabel();
            UpdateSearchPanelState();
        };

        searchPanel.Controls.Add(lstCustomerResults);
        searchPanel.Controls.Add(txtCustomerSearch);

        lblSelectedCustomer.Dock = DockStyle.Top;
        lblSelectedCustomer.Height = 26;
        lblSelectedCustomer.ForeColor = UiTheme.TextMuted;
        lblSelectedCustomer.TextAlign = ContentAlignment.MiddleLeft;
        lblSelectedCustomer.Padding = new Padding(12, 2, 12, 0);
        lblSelectedCustomer.Text = "No customer selected yet.";

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

        // Real cover letters this issue actually has on file, same as
        // PullListEntryForm - still editable since a brand new variant
        // can show up later that hasn't been seen yet.
        foreach (var letter in knownCoverLetters)
            cboCover.Items.Add(letter);
        cboCover.Text = defaultCoverLetter;
        cboCover.MaxLength = 1;

        AddRow("Cover:*", cboCover, 30);
        AddRow("Quantity:*", txtQuantity, 30);
        AddRow("Notes:", txtNotes, 64);

        // rest3: lblSelectedCustomer on top, the field layout fills what's left.
        var rest3 = new Panel { Dock = DockStyle.Fill };
        rest3.Controls.Add(layout);
        rest3.Controls.Add(lblSelectedCustomer);

        // rest2: the customer search panel (fixed height) on top, rest3 fills what's left.
        var rest2 = new Panel { Dock = DockStyle.Fill };
        rest2.Controls.Add(rest3);
        rest2.Controls.Add(searchPanel);

        // middle: the "which customer" prompt on top, rest2 fills what's left.
        var middle = new Panel { Dock = DockStyle.Fill };
        middle.Controls.Add(rest2);
        middle.Controls.Add(lblCustomerPrompt);

        Controls.Add(middle);
        Controls.Add(footer);
        Controls.Add(lblIssue);

        UpdateSearchPanelState();

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);

        txtCustomerSearch.Focus();
    }

    private void UpdateSelectedCustomerLabel()
    {
        lblSelectedCustomer.Text = _selectedCustomer == null
            ? "No customer selected yet."
            : $"Selected: {_selectedCustomer.Name}";
    }

    // searchPanel is fixed-height (Dock=Top), so the results list staying
    // visible after a pick doesn't just look odd - it keeps eating 140px
    // of this dialog's limited height, which is what was crowding Hold
    // It/Cancel down near the bottom edge. Collapse it the moment a
    // selection exists (or there's nothing to show), and only expand it
    // back out while actively searching with no pick made yet.
    private void UpdateSearchPanelState()
    {
        var showResults = _selectedCustomer == null && _searchResults.Count > 0;
        lstCustomerResults.Visible = showResults;
        searchPanel.Height = showResults ? 140 : 30;
    }

    private void TxtCustomerSearch_TextChanged(object? sender, EventArgs e)
    {
        var text = txtCustomerSearch.Text;
        _searchResults = string.IsNullOrWhiteSpace(text) ? new List<Customer>() : _customers.SearchCustomers(text);

        lstCustomerResults.Items.Clear();
        foreach (var customer in _searchResults)
            lstCustomerResults.Items.Add(customer.Name);

        _selectedCustomer = null;
        UpdateSelectedCustomerLabel();
        UpdateSearchPanelState();
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        lblError.Text = "";

        if (_selectedCustomer == null)
        {
            lblError.Text = "Pick a customer from the search results first.";
            return;
        }

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

        CustomerId = _selectedCustomer.Id;
        CoverLetter = cover;
        Quantity = qty;
        Notes = string.IsNullOrWhiteSpace(txtNotes.Text) ? null : txtNotes.Text.Trim();

        DialogResult = DialogResult.OK;
        Close();
    }
}
