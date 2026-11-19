using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class SettingsControl : UserControl
{
    private readonly IEmailSettingsService _emailSettingsService;
    private readonly IStoreSettingsService _storeSettingsService;
    private readonly IBackupService _backupService;

    private readonly TextBox txtStoreName = new();
    private readonly TextBox txtAddress = new();
    private readonly TextBox txtPhone = new();
    private readonly TextBox txtTaxRate = new();

    private readonly PictureBox picLogoPreview = new();
    private readonly Button btnChooseLogo = new();
    private readonly Button btnClearLogo = new();
    private string? _logoPath;

    private readonly TextBox txtSmtpHost = new();
    private readonly TextBox txtSmtpPort = new();
    private readonly TextBox txtSmtpUsername = new();
    private readonly TextBox txtSmtpPassword = new();
    private readonly CheckBox chkUseSsl = new();
    private readonly TextBox txtFromEmail = new();
    private readonly TextBox txtFromDisplayName = new();
    private readonly TextBox txtAdminEmail = new();
    private readonly CheckBox chkAutoSendFocReport = new();
    private readonly TextBox txtFocReportDaysAhead = new();
    private readonly TextBox txtComicVineApiKey = new();
    private readonly TextBox txtTrackedPublishers = new();
    private readonly Button btnSave = new();
    private readonly Button btnBackup = new();
    private readonly Label lblStatus = new();

    public SettingsControl(IEmailSettingsService emailSettingsService, IStoreSettingsService storeSettingsService, IBackupService backupService)
    {
        _emailSettingsService = emailSettingsService;
        _storeSettingsService = storeSettingsService;
        _backupService = backupService;

        Dock = DockStyle.Fill;
        Padding = new Padding(12);
        AutoScroll = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 760,
            ColumnCount = 2,
            RowCount = 19
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control input, int height = 32)
        {
            layout.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, layout.RowStyles.Count);
            input.Dock = DockStyle.Fill;
            layout.Controls.Add(input, 1, layout.RowStyles.Count);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        }

        void AddSectionHeader(string text)
        {
            // MiddleLeft + generous row height avoids clipping the top of
            // the bold 14pt text, which a bottom-anchored label in a tight
            // fixed-height row was prone to at some Windows DPI scales.
            var header = new Label { Text = text, Font = UiTheme.HeaderFont, ForeColor = UiTheme.PrimaryDark, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 10, 0, 0) };
            layout.Controls.Add(header, 0, layout.RowStyles.Count);
            layout.SetColumnSpan(header, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        }

        txtSmtpPassword.PasswordChar = '*';

        AddSectionHeader("Store");
        AddRow("Store Name:", txtStoreName);
        AddRow("Address:", txtAddress);
        AddRow("Phone:", txtPhone);
        AddRow("Sales Tax Rate (%):", txtTaxRate);

        picLogoPreview.Width = 160;
        picLogoPreview.Height = 70;
        picLogoPreview.SizeMode = PictureBoxSizeMode.Zoom;
        picLogoPreview.BorderStyle = BorderStyle.FixedSingle;
        picLogoPreview.BackColor = Color.White;

        var logoButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 0, 0, 0) };
        btnChooseLogo.Text = "Choose Logo...";
        btnChooseLogo.AutoSize = true;
        btnChooseLogo.Height = 30;
        btnChooseLogo.Click += BtnChooseLogo_Click;
        btnClearLogo.Text = "Clear";
        btnClearLogo.AutoSize = true;
        btnClearLogo.Height = 30;
        btnClearLogo.Margin = new Padding(8, 0, 0, 0);
        btnClearLogo.Click += (s, e) => { _logoPath = null; picLogoPreview.Image = null; };
        logoButtons.Controls.Add(btnChooseLogo);
        logoButtons.Controls.Add(btnClearLogo);

        var logoRow = new Panel { Dock = DockStyle.Fill };
        logoButtons.Dock = DockStyle.Fill;
        picLogoPreview.Dock = DockStyle.Left;
        logoRow.Controls.Add(logoButtons);
        logoRow.Controls.Add(picLogoPreview);
        AddRow("Receipt Logo:", logoRow, 80);

        AddSectionHeader("Email (FOC Report)");
        AddRow("SMTP Host:", txtSmtpHost);
        AddRow("SMTP Port:", txtSmtpPort);
        AddRow("SMTP Username:", txtSmtpUsername);
        AddRow("SMTP Password:", txtSmtpPassword);
        AddRow("Use SSL:", chkUseSsl);
        AddRow("From Email:", txtFromEmail);
        AddRow("From Display Name:", txtFromDisplayName);
        AddRow("Admin Email (recipient):", txtAdminEmail);
        AddRow("Auto-email FOC report daily:", chkAutoSendFocReport);
        AddRow("Auto report days ahead:", txtFocReportDaysAhead);
        var autoFocHelp = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.TextMuted,
            TextAlign = ContentAlignment.TopLeft,
            Text = "When checked, the app emails the admin a FOC buy report every day (covering this many days ahead) with no need to click \"Send Email to Admin\" yourself. Requires SMTP + Admin Email above to be filled in."
        };
        layout.Controls.Add(new Label(), 0, layout.RowStyles.Count);
        layout.Controls.Add(autoFocHelp, 1, layout.RowStyles.Count);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        AddSectionHeader("New Releases (Comic Vine)");
        AddRow("Comic Vine API Key:", txtComicVineApiKey);
        var cvHelp = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.TextMuted,
            TextAlign = ContentAlignment.TopLeft,
            Text = "Free - get one at comicvine.gamespot.com/api/. Used by the New Releases tab to find issues you don't have yet."
        };
        layout.Controls.Add(new Label(), 0, layout.RowStyles.Count);
        layout.Controls.Add(cvHelp, 1, layout.RowStyles.Count);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        AddRow("Tracked Publishers:", txtTrackedPublishers);
        var tpHelp = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.TextMuted,
            TextAlign = ContentAlignment.TopLeft,
            Text = "Comma-separated (e.g. Marvel, DC Comics, Image Comics) - must match Comic Vine\u2019s exact publisher name. Strongly recommended: makes the weekly search faster and relevant. Leave blank to search ALL publishers on Comic Vine (mostly manga/small press for a US shop - slow and noisy)."
        };
        layout.Controls.Add(new Label(), 0, layout.RowStyles.Count);
        layout.Controls.Add(tpHelp, 1, layout.RowStyles.Count);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        btnSave.Text = "Save Settings";
        btnSave.Width = 130;
        btnSave.Height = 34;
        btnSave.Tag = "primary";
        btnSave.Click += BtnSave_Click;

        btnBackup.Text = "Backup Database Now";
        btnBackup.Width = 170;
        btnBackup.Height = 34;
        btnBackup.Margin = new Padding(10, 0, 0, 0);
        btnBackup.Click += BtnBackup_Click;

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(0, 8, 0, 8) };
        buttonPanel.Controls.Add(btnSave);
        buttonPanel.Controls.Add(btnBackup);

        lblStatus.Dock = DockStyle.Top;
        lblStatus.Height = 24;
        lblStatus.ForeColor = Color.Green;
        lblStatus.Font = UiTheme.BoldFont;

        var helpLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 50,
            ForeColor = UiTheme.TextMuted,
            Text = "For Gmail: smtp.gmail.com, port 587, SSL on, and use an App Password (not your normal password) as SMTP Password.\r\nSettings are saved locally on this machine only, in plain text."
        };

        // Dock=Top siblings stack with the LAST control added ending up
        // closest to the top edge, so to get the natural reading order
        // (fields, then status, then buttons, then help text at the very
        // bottom) they have to be added in the reverse of that order.
        Controls.Add(helpLabel);
        Controls.Add(buttonPanel);
        Controls.Add(lblStatus);
        Controls.Add(layout);

        LoadSettings();

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
    }

    private void BtnChooseLogo_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*",
            Title = "Choose a logo image"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            using var fs = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read);
            picLogoPreview.Image = Image.FromStream(fs);
            _logoPath = dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't load that image: {ex.Message}", "Invalid Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadSettings()
    {
        var store = _storeSettingsService.Load();
        txtStoreName.Text = store.StoreName;
        txtAddress.Text = store.Address;
        txtPhone.Text = store.Phone;
        txtTaxRate.Text = store.TaxRatePercent.ToString();
        _logoPath = store.LogoPath;

        if (!string.IsNullOrWhiteSpace(_logoPath) && File.Exists(_logoPath))
        {
            try
            {
                using var fs = new FileStream(_logoPath, FileMode.Open, FileAccess.Read);
                picLogoPreview.Image = Image.FromStream(fs);
            }
            catch
            {
                // Ignore a logo file that's gone missing or corrupt - just show no preview.
            }
        }

        var email = _emailSettingsService.Load();
        txtSmtpHost.Text = email.SmtpHost;
        txtSmtpPort.Text = email.SmtpPort.ToString();
        txtSmtpUsername.Text = email.SmtpUsername;
        txtSmtpPassword.Text = email.SmtpPassword;
        chkUseSsl.Checked = email.UseSsl;
        txtFromEmail.Text = email.FromEmail;
        txtFromDisplayName.Text = email.FromDisplayName;
        txtAdminEmail.Text = email.AdminEmail;
        chkAutoSendFocReport.Checked = email.AutoSendFocReport;
        txtFocReportDaysAhead.Text = email.FocReportDaysAhead.ToString();

        txtComicVineApiKey.Text = store.ComicVineApiKey ?? "";
        txtTrackedPublishers.Text = store.TrackedPublishers ?? "";
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        if (!int.TryParse(txtSmtpPort.Text, out var port))
        {
            MessageBox.Show("SMTP Port must be a number.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!decimal.TryParse(txtTaxRate.Text, out var taxRate) || taxRate < 0)
        {
            MessageBox.Show("Tax rate must be a non-negative number.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _storeSettingsService.Save(new StoreSettings
        {
            StoreName = string.IsNullOrWhiteSpace(txtStoreName.Text) ? "Comic POS" : txtStoreName.Text.Trim(),
            Address = txtAddress.Text.Trim(),
            Phone = txtPhone.Text.Trim(),
            TaxRatePercent = taxRate,
            LogoPath = _logoPath,
            ComicVineApiKey = string.IsNullOrWhiteSpace(txtComicVineApiKey.Text) ? null : txtComicVineApiKey.Text.Trim(),
            TrackedPublishers = string.IsNullOrWhiteSpace(txtTrackedPublishers.Text) ? null : txtTrackedPublishers.Text.Trim(),
            // Preserve this - it's not editable here, and overwriting it with
            // null on every settings save would make the weekly New Books
            // check think it's never run, forcing an extra check on the next
            // startup/Sunday tick (same bug class as LastFocReportSentAt below).
            LastAutoFetchRun = _storeSettingsService.Load().LastAutoFetchRun
        });

        _emailSettingsService.Save(new EmailSettings
        {
            SmtpHost = txtSmtpHost.Text.Trim(),
            SmtpPort = port,
            SmtpUsername = txtSmtpUsername.Text.Trim(),
            SmtpPassword = txtSmtpPassword.Text,
            UseSsl = chkUseSsl.Checked,
            FromEmail = txtFromEmail.Text.Trim(),
            FromDisplayName = txtFromDisplayName.Text.Trim(),
            AdminEmail = txtAdminEmail.Text.Trim(),
            AutoSendFocReport = chkAutoSendFocReport.Checked,
            FocReportDaysAhead = int.TryParse(txtFocReportDaysAhead.Text, out var focDays) && focDays > 0 ? focDays : 14,
            LastFocReportSentAt = _emailSettingsService.Load().LastFocReportSentAt
        });

        lblStatus.Text = "Saved.";
    }

    private void BtnBackup_Click(object? sender, EventArgs e)
    {
        try
        {
            var path = _backupService.BackupNow();
            MessageBox.Show($"Backup saved to:\n{path}", "Backup Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Backup failed: {ex.Message}", "Backup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
