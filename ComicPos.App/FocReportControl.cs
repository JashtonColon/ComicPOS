using ComicPos.Core.Services;

namespace ComicPos.App;

public class FocReportControl : UserControl
{
    private readonly IFocReportService _focReport;
    private readonly IEmailService _email;
    private readonly IEmailSettingsService _emailSettings;

    private readonly Panel topPanel;
    private readonly TextBox txtDaysAhead = new();
    private readonly Button btnGenerate = new();
    private readonly Button btnSendEmail = new();
    private readonly DataGridView grid = new();

    private List<FocReportEntry> _entries = new();

    public FocReportControl(IFocReportService focReport, IEmailService email, IEmailSettingsService emailSettings)
    {
        _focReport = focReport;
        _email = email;
        _emailSettings = emailSettings;

        Dock = DockStyle.Fill;

        topPanel = new Panel { Dock = DockStyle.Top, Height = 40 };

        btnSendEmail.Dock = DockStyle.Right;
        btnSendEmail.Width = 150;
        btnSendEmail.Text = "Send Email to Admin";
        btnSendEmail.Click += BtnSendEmail_Click;

        btnGenerate.Dock = DockStyle.Right;
        btnGenerate.Width = 90;
        btnGenerate.Text = "Generate";
        btnGenerate.Click += (s, e) => GenerateReport();

        txtDaysAhead.Dock = DockStyle.Right;
        txtDaysAhead.Width = 50;
        txtDaysAhead.Text = "30";
        txtDaysAhead.TextAlign = HorizontalAlignment.Center;

        var lblDays = new Label { Dock = DockStyle.Right, Width = 110, TextAlign = ContentAlignment.MiddleRight, Text = "Days ahead:" };

        topPanel.Controls.Add(btnSendEmail);
        topPanel.Controls.Add(btnGenerate);
        topPanel.Controls.Add(txtDaysAhead);
        topPanel.Controls.Add(lblDays);

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FocDate", HeaderText = "FOC Date", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Series", HeaderText = "Series", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Issue", HeaderText = "Issue #", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Buy", HeaderText = "Buy Qty", ReadOnly = true });
        grid.CellDoubleClick += Grid_CellDoubleClick;

        Controls.Add(grid);
        Controls.Add(topPanel);

        GenerateReport();
    }

    private void GenerateReport()
    {
        if (!int.TryParse(txtDaysAhead.Text, out var days) || days <= 0)
        {
            MessageBox.Show("Days ahead must be a positive number.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _entries = _focReport.GetUpcomingFocReport(days);

        grid.Rows.Clear();
        foreach (var entry in _entries)
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["FocDate"].Value = entry.FocDate?.ToString("MM/dd/yyyy") ?? "";
            row.Cells["Series"].Value = $"{entry.SeriesName} ({entry.StartYear})";
            row.Cells["Issue"].Value = entry.IssueNumber.ToString("0.##");
            row.Cells["Buy"].Value = entry.Subscribers.Count;
            row.Tag = entry;
        }
    }

    private void Grid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        if (grid.Rows[e.RowIndex].Tag is not FocReportEntry entry) return;

        var names = string.Join("\n", entry.Subscribers.Select(s => $"{s.CustomerName}  [Cover {s.PreferredCover}]"));
        MessageBox.Show(names, $"{entry.SeriesName} #{entry.IssueNumber:0.##} - Subscribers", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BtnSendEmail_Click(object? sender, EventArgs e)
    {
        var settings = _emailSettings.Load();

        if (string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.AdminEmail))
        {
            MessageBox.Show("Email isn't configured yet. Go to the Settings tab and fill in your SMTP details first.", "Not Configured", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _email.SendFocReport(_entries, settings);
            MessageBox.Show($"Report sent to {settings.AdminEmail}.", "Sent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to send email:\n{ex.Message}", "Send Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
