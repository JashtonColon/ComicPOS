using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class SettingsControl : UserControl
{
    private readonly IEmailSettingsService _settingsService;

    private readonly TextBox txtSmtpHost = new();
    private readonly TextBox txtSmtpPort = new();
    private readonly TextBox txtSmtpUsername = new();
    private readonly TextBox txtSmtpPassword = new();
    private readonly CheckBox chkUseSsl = new();
    private readonly TextBox txtFromEmail = new();
    private readonly TextBox txtFromDisplayName = new();
    private readonly TextBox txtAdminEmail = new();
    private readonly Button btnSave = new();
    private readonly Label lblStatus = new();

    public SettingsControl(IEmailSettingsService settingsService)
    {
        _settingsService = settingsService;

        Dock = DockStyle.Fill;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 300,
            ColumnCount = 2,
            RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control input)
        {
            var row = layout.RowCount;
            layout.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, layout.RowStyles.Count);
            input.Dock = DockStyle.Fill;
            layout.Controls.Add(input, 1, layout.RowStyles.Count);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        }

        txtSmtpPassword.PasswordChar = '*';

        AddRow("SMTP Host:", txtSmtpHost);
        AddRow("SMTP Port:", txtSmtpPort);
        AddRow("SMTP Username:", txtSmtpUsername);
        AddRow("SMTP Password:", txtSmtpPassword);
        AddRow("Use SSL:", chkUseSsl);
        AddRow("From Email:", txtFromEmail);
        AddRow("From Display Name:", txtFromDisplayName);
        AddRow("Admin Email (recipient):", txtAdminEmail);

        btnSave.Text = "Save Settings";
        btnSave.Width = 120;
        btnSave.Height = 32;
        btnSave.Click += BtnSave_Click;

        var buttonPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
        buttonPanel.Controls.Add(btnSave);

        lblStatus.Dock = DockStyle.Top;
        lblStatus.Height = 24;
        lblStatus.ForeColor = Color.Green;

        var helpLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            Text = "For Gmail: smtp.gmail.com, port 587, SSL on, and use an App Password (not your normal password) as SMTP Password.\r\nSettings are saved locally on this machine only, in plain text."
        };

        Controls.Add(layout);
        Controls.Add(lblStatus);
        Controls.Add(buttonPanel);
        Controls.Add(helpLabel);

        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();
        txtSmtpHost.Text = settings.SmtpHost;
        txtSmtpPort.Text = settings.SmtpPort.ToString();
        txtSmtpUsername.Text = settings.SmtpUsername;
        txtSmtpPassword.Text = settings.SmtpPassword;
        chkUseSsl.Checked = settings.UseSsl;
        txtFromEmail.Text = settings.FromEmail;
        txtFromDisplayName.Text = settings.FromDisplayName;
        txtAdminEmail.Text = settings.AdminEmail;
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        if (!int.TryParse(txtSmtpPort.Text, out var port))
        {
            MessageBox.Show("SMTP Port must be a number.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var settings = new EmailSettings
        {
            SmtpHost = txtSmtpHost.Text.Trim(),
            SmtpPort = port,
            SmtpUsername = txtSmtpUsername.Text.Trim(),
            SmtpPassword = txtSmtpPassword.Text,
            UseSsl = chkUseSsl.Checked,
            FromEmail = txtFromEmail.Text.Trim(),
            FromDisplayName = txtFromDisplayName.Text.Trim(),
            AdminEmail = txtAdminEmail.Text.Trim()
        };

        _settingsService.Save(settings);
        lblStatus.Text = "Saved.";
    }
}
