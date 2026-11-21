using ComicPos.Core.Services;

namespace ComicPos.App;

// Modal "add a customer" dialog - used to replace the old inline
// name/email/phone row that used to live pinned to the bottom of the
// Customers list. That row was a fixed Height=35 Panel, which was fine
// back when buttons and textboxes were short and plain; once
// UiTheme.StyleButton grew every button's height floor and padding, the
// row's content no longer fit in 35px and spilled up into the grid above
// it. Rather than just re-measuring that panel again, this makes "add a
// customer" its own small popup (same shape as HoldForm/PullListEntryForm)
// so the list itself no longer has to reserve permanent screen space for
// a form that's only in use for a few seconds at a time.
public class AddCustomerForm : Form
{
    private readonly TextBox txtName = new();
    private readonly TextBox txtEmail = new();
    private readonly TextBox txtPhone = new();
    private readonly Button btnSave = new();
    private readonly Button btnCancel = new();
    private readonly Label lblError = new();

    public string CustomerName { get; private set; } = "";
    public string? Email { get; private set; }
    public string? Phone { get; private set; }

    public AddCustomerForm()
    {
        Text = "Add Customer";
        Width = 380;
        Height = 300;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var lblTitle = new Label
        {
            Text = "New Customer",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = UiTheme.HeaderFont,
            ForeColor = UiTheme.PrimaryDark,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 10, 12, 0)
        };

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
        btnSave.Text = "Add Customer";
        btnSave.Width = 120;
        btnSave.Click += BtnSave_Click;
        btnSave.Tag = "primary";
        buttonPanel.Controls.Add(btnCancel);
        buttonPanel.Controls.Add(btnSave);
        footer.Controls.Add(buttonPanel);
        footer.Controls.Add(lblError);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12, 12, 12, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control input, int height)
        {
            layout.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, layout.RowStyles.Count);
            input.Dock = DockStyle.Fill;
            layout.Controls.Add(input, 1, layout.RowStyles.Count);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        }

        txtName.PlaceholderText = "Full name";
        txtEmail.PlaceholderText = "email@example.com";
        txtPhone.PlaceholderText = "555-0100";

        AddRow("Name:*", txtName, 34);
        AddRow("Email:", txtEmail, 34);
        AddRow("Phone:", txtPhone, 34);

        Controls.Add(layout);
        Controls.Add(footer);
        Controls.Add(lblTitle);

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);

        txtName.Focus();
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        lblError.Text = "";

        var name = txtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            lblError.Text = "Name is required.";
            return;
        }

        CustomerName = name;
        Email = string.IsNullOrWhiteSpace(txtEmail.Text) ? null : txtEmail.Text.Trim();
        Phone = string.IsNullOrWhiteSpace(txtPhone.Text) ? null : txtPhone.Text.Trim();

        DialogResult = DialogResult.OK;
        Close();
    }
}
