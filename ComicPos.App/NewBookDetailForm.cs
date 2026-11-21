using ComicPos.Core.Models;

namespace ComicPos.App;

// Opened by double-clicking a row in the New Books grid (AutoFetchControl).
// Read-only - this is "what is this book, what covers does it have, what
// will it cost", not an editing screen (editing covers/barcodes/stock
// happens in Inventory once a book has actually arrived).
public class NewBookDetailForm : Form
{
    public NewBookDetailForm(Issue issue)
    {
        Text = $"{issue.Series.Name} #{issue.IssueNumber:0.##}";
        Width = 720;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(480, 360);

        var header = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(16, 12, 16, 8) };

        var lblTitle = new Label
        {
            Dock = DockStyle.Top,
            // AutoSize, not a fixed Height - see InventoryControl's
            // lblBreadcrumb for why a hardcoded pixel value here clipped
            // the text. AutoEllipsis still only affects width, which Dock
            // continues to constrain.
            AutoSize = true,
            Font = UiTheme.HeaderFont,
            AutoEllipsis = true,
            Text = $"{issue.Series.Name} ({issue.Series.StartYear}) #{issue.IssueNumber:0.##}"
        };

        var subParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(issue.Publisher)) subParts.Add(issue.Publisher);
        subParts.Add(issue.Type == IssueType.CollectedEdition ? "Collected Edition" : "Single Issue");
        if (issue.FocDate != null) subParts.Add($"FOC {issue.FocDate:MM/dd/yyyy}");
        if (issue.ReleaseDate != null) subParts.Add($"Street {issue.ReleaseDate:MM/dd/yyyy}");

        var lblSub = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = UiTheme.TextMuted,
            Text = string.Join("   •   ", subParts)
        };

        header.Controls.Add(lblSub);
        header.Controls.Add(lblTitle);

        var lblCoversHeading = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(16, 6, 0, 0),
            Font = UiTheme.BoldFont,
            Text = issue.Covers.Count == 1 ? "Cover" : $"Covers ({issue.Covers.Count})"
        };

        var coverFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(12)
        };

        foreach (var cover in issue.Covers.OrderBy(c => c.CoverLetter))
            coverFlow.Controls.Add(BuildCoverCard(cover));

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var btnClose = new Button { Text = "Close", Width = 100, Height = 32, DialogResult = DialogResult.OK };
        buttonPanel.Controls.Add(btnClose);

        Controls.Add(coverFlow);
        Controls.Add(lblCoversHeading);
        Controls.Add(buttonPanel);
        Controls.Add(header);

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
    }

    private static Panel BuildCoverCard(Cover cover)
    {
        var card = new Panel
        {
            Width = 180,
            Height = 300,
            Margin = new Padding(8),
            Padding = new Padding(10),
            BackColor = UiTheme.PanelBackground,
            BorderStyle = BorderStyle.FixedSingle
        };

        var pic = new PictureBox
        {
            Dock = DockStyle.Top,
            Height = 170,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = UiTheme.RowAlt,
            Cursor = Cursors.Hand
        };
        pic.Image = LoadCoverImage(cover) ?? UiTheme.BuildNoCoverPlaceholder(160, 170);
        // Click to see it bigger - the 160x170 thumbnail is too small to
        // actually check variant art or read cover text off of.
        pic.Click += (s, e) =>
        {
            if (pic.Image != null)
                UiTheme.ShowImageZoom(pic.FindForm(), pic.Image, cover.CoverLetter == "A" ? "Cover A (main)" : $"Cover {cover.CoverLetter}");
        };

        var lblLetter = new Label
        {
            Dock = DockStyle.Top,
            // AutoSize, not a fixed Height - a hardcoded 20px on lblPrice
            // below clipped the descender off "yet" (rendering as "vet")
            // once real DPI scaling applied; same bug class as the header
            // labels fixed earlier, just on a plain-weight line this time.
            AutoSize = true,
            Font = UiTheme.BoldFont,
            Text = cover.CoverLetter == "A" ? "Cover A (main)" : $"Cover {cover.CoverLetter}"
        };

        var lblPrice = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = UiTheme.TextMuted,
            Text = cover.Price > 0 ? cover.Price.ToString("C") : "Price not set yet"
        };

        var lblDescription = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = UiTheme.TextPrimary,
            Text = string.IsNullOrWhiteSpace(cover.Description) ? "No description available yet." : cover.Description
        };

        card.Controls.Add(lblDescription);
        card.Controls.Add(lblPrice);
        card.Controls.Add(lblLetter);
        card.Controls.Add(pic);
        return card;
    }

    private static Image? LoadCoverImage(Cover cover)
    {
        if (string.IsNullOrWhiteSpace(cover.CoverImagePath) || !File.Exists(cover.CoverImagePath)) return null;
        try
        {
            using var fs = new FileStream(cover.CoverImagePath, FileMode.Open, FileAccess.Read);
            return Image.FromStream(fs);
        }
        catch
        {
            // Missing/corrupt cached image - fall back to the placeholder.
            return null;
        }
    }
}
