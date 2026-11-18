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
            "Import new comics from a solicitation CSV.\r\n" +
            "Required columns: Series, IssueNumber, Barcode. Optional: StartYear, Publisher, CoverLetter, Price, FocDate, ReleaseDate, Type (SingleIssue/CollectedEdition).\r\n" +
            "Re-importing the same file is safe - existing series/issues/covers are matched and updated, never duplicated.";

        topPanel = new Panel { Dock = DockStyle.Top, Height = 40 };

        btnRunImport.Dock = DockStyle.Right;
        btnRunImport.Width = 110;
        btnRunImport.Text = "Run Import";
        btnRunImport.Enabled = false;
        btnRunImport.Click += BtnRunImport_Click;

        btnChooseFile.Dock = DockStyle.Right;
        btnChooseFile.Width = 110;
        btnChooseFile.Text = "Choose File...";
        btnChooseFile.Click += BtnChooseFile_Click;

        lblFilePath.Dock = DockStyle.Fill;
        lblFilePath.TextAlign = ContentAlignment.MiddleLeft;
        lblFilePath.Text = "No file selected.";

        topPanel.Controls.Add(lblFilePath);
        topPanel.Controls.Add(btnRunImport);
        topPanel.Controls.Add(btnChooseFile);

        txtResults.Dock = DockStyle.Fill;
        txtResults.Multiline = true;
        txtResults.ReadOnly = true;
        txtResults.ScrollBars = ScrollBars.Vertical;
        txtResults.Font = new Font("Consolas", 9);

        Controls.Add(txtResults);
        Controls.Add(topPanel);
        Controls.Add(lblHelp);
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
