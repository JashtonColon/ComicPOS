using ComicPos.Core.Services;

namespace ComicPos.App;

public class ImportControl : UserControl
{
    private readonly IImportService _import;

    private readonly Label lblHelp = new();
    private readonly Panel topPanel;
    private readonly Label lblFilePath = new();
    private readonly Button btnChooseFile = new();
    private readonly Button btnRunImport = new();
    private readonly TextBox txtResults = new();

    private string? _selectedFilePath;

    public ImportControl(IImportService import)
    {
        _import = import;

        Dock = DockStyle.Fill;

        lblHelp.Dock = DockStyle.Top;
        lblHelp.Height = 60;
        lblHelp.Padding = new Padding(8);
        lblHelp.Text =
            "Import new comics from a solicitation CSV, or receive a shipment of comics already in the system.\r\n" +
            "Required columns: Series, IssueNumber, Barcode. Optional: StartYear, Publisher, CoverLetter, Price, FocDate, ReleaseDate, Type (SingleIssue/CollectedEdition), ReceivedQty.\r\n" +
            "ReceivedQty adds to stock (0 if omitted - solicited but not arrived yet). Re-importing the same file is safe - existing series/issues/covers are matched and updated, never duplicated.";

        // Grown from 40 - the now-taller, rounded buttons (UiTheme.StyleButton's
        // MinButtonHeight + padding) didn't fit in the old fixed height, and
        // "Choose File..." is wide enough at the new bold font that its
        // AutoSize/GrowOnly width could exceed a too-tight hardcoded Width,
        // so both button widths got a bit of headroom too.
        topPanel = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(0, 8, 0, 8) };

        btnRunImport.Dock = DockStyle.Right;
        btnRunImport.Width = 130;
        btnRunImport.Text = "Run Import";
        btnRunImport.Enabled = false;
        btnRunImport.Click += BtnRunImport_Click;
        btnRunImport.Tag = "primary";

        btnChooseFile.Dock = DockStyle.Right;
        btnChooseFile.Width = 140;
        btnChooseFile.Text = "Choose File...";
        btnChooseFile.Click += BtnChooseFile_Click;

        lblFilePath.Dock = DockStyle.Fill;
        lblFilePath.TextAlign = ContentAlignment.MiddleLeft;
        lblFilePath.Text = "No file selected.";

        // Dock=Right siblings stack with the LAST control added ending up
        // closest to the right edge, so "Choose File..." (the first step)
        // has to be added before "Run Import" (the final step) to read
        // left-to-right in that order.
        topPanel.Controls.Add(lblFilePath);
        topPanel.Controls.Add(btnChooseFile);
        topPanel.Controls.Add(btnRunImport);

        txtResults.Dock = DockStyle.Fill;
        txtResults.Multiline = true;
        txtResults.ReadOnly = true;
        txtResults.ScrollBars = ScrollBars.Vertical;
        txtResults.Font = new Font("Consolas", 9);

        Controls.Add(txtResults);
        Controls.Add(topPanel);
        Controls.Add(lblHelp);

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
    }

    private void BtnChooseFile_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            Title = "Choose a solicitation CSV"
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _selectedFilePath = dialog.FileName;
            lblFilePath.Text = _selectedFilePath;
            btnRunImport.Enabled = true;
        }
    }

    private void BtnRunImport_Click(object? sender, EventArgs e)
    {
        if (_selectedFilePath == null) return;

        var summary = _import.ImportFromCsv(_selectedFilePath);

        var lines = new List<string>
        {
            $"Imported: {_selectedFilePath}",
            "",
            $"Series created:   {summary.SeriesCreated}",
            $"Issues created:   {summary.IssuesCreated}",
            $"Covers created:   {summary.CoversCreated}",
            $"Covers updated:   {summary.CoversUpdated}",
            ""
        };

        if (summary.Errors.Count > 0)
        {
            lines.Add($"Warnings/errors ({summary.Errors.Count}):");
            lines.AddRange(summary.Errors);
        }
        else
        {
            lines.Add("No errors.");
        }

        txtResults.Text = string.Join(Environment.NewLine, lines);
    }
}
