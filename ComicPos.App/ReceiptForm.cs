using System.Drawing.Printing;

namespace ComicPos.App;

// Shown after a sale completes. Styled to actually look like a receipt - a
// narrow white "paper" card, centered logo/store name/address/phone header,
// then a monospace itemized body with right-aligned amounts - instead of
// dumping raw text in a plain textbox.
public class ReceiptForm : Form
{
    private readonly string _itemizedBody;
    private readonly string _storeName;
    private readonly string _address;
    private readonly string _phone;
    private readonly Image? _logo;
    private readonly Button btnPrint = new();
    private readonly Button btnClose = new();

    public ReceiptForm(string itemizedBody, string storeName, string address, string phone, string? logoPath = null)
    {
        _itemizedBody = itemizedBody;
        _storeName = storeName;
        _address = address;
        _phone = phone;
        _logo = TryLoadLogo(logoPath);

        Text = "Receipt";
        Width = 420;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(226, 229, 233);
        Font = UiTheme.BaseFont;

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        btnClose.Text = "Close";
        btnClose.Width = 90;
        btnClose.Height = 32;
        btnClose.DialogResult = DialogResult.OK;
        btnPrint.Text = "Print Receipt";
        btnPrint.Width = 120;
        btnPrint.Height = 32;
        btnPrint.Tag = "primary";
        btnPrint.Click += BtnPrint_Click;
        buttonPanel.Controls.Add(btnClose);
        buttonPanel.Controls.Add(btnPrint);

        // A scrollable gray "counter" the white receipt card sits on,
        // same idea as a document preview - makes the card read as paper.
        var scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(226, 229, 233), Padding = new Padding(20) };

        var card = new Panel
        {
            Width = 340,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.White,
            BorderStyle = BorderStyle.None,
            Padding = new Padding(20, 18, 20, 18),
            Left = 0,
            Top = 0
        };

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Width = 300
        };

        if (_logo != null)
        {
            var picLogo = new PictureBox
            {
                Image = _logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = 140,
                Height = (int)(140 * (_logo.Height / (float)_logo.Width)),
                Margin = new Padding((300 - 140) / 2, 0, 0, 8)
            };
            if (picLogo.Height > 110) { picLogo.Height = 110; picLogo.Width = (int)(110 * (_logo.Width / (float)_logo.Height)); picLogo.Margin = new Padding((300 - picLogo.Width) / 2, 0, 0, 8); }
            stack.Controls.Add(picLogo);
        }

        stack.Controls.Add(CenteredLabel(string.IsNullOrWhiteSpace(_storeName) ? "Comic POS" : _storeName, 13f, FontStyle.Bold, UiTheme.TextPrimary, 300));
        if (!string.IsNullOrWhiteSpace(_address)) stack.Controls.Add(CenteredLabel(_address, 8.5f, FontStyle.Regular, UiTheme.TextMuted, 300));
        if (!string.IsNullOrWhiteSpace(_phone)) stack.Controls.Add(CenteredLabel(_phone, 8.5f, FontStyle.Regular, UiTheme.TextMuted, 300));

        var bodyBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            Text = _itemizedBody.TrimEnd('\r', '\n'),
            Width = 300,
            Margin = new Padding(0, 10, 0, 10)
        };
        // Size the textbox to its content so the card isn't full of dead
        // space, and so it never shows its own scrollbar inside the card.
        using (var g = CreateGraphics())
        {
            var lineCount = _itemizedBody.Split('\n').Length;
            bodyBox.Height = (int)(g.MeasureString("Ay", bodyBox.Font).Height * lineCount) + 6;
        }
        bodyBox.GotFocus += (s, e) => bodyBox.DeselectAll();
        stack.Controls.Add(bodyBox);

        stack.Controls.Add(CenteredLabel("Thank you for shopping with us!", 9f, FontStyle.Italic, UiTheme.TextMuted, 300, new Padding(0, 4, 0, 0)));

        card.Controls.Add(stack);
        scrollHost.Controls.Add(card);

        Controls.Add(scrollHost);
        Controls.Add(buttonPanel);

        AcceptButton = btnClose;
        ActiveControl = btnClose;

        UiTheme.StyleButton(btnPrint, primary: true);
        UiTheme.StyleButton(btnClose, primary: false);
    }

    private static Label CenteredLabel(string text, float size, FontStyle style, Color color, int width, Padding? margin = null)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI", size, style),
            ForeColor = color,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Width = width,
            Height = (int)(size * 2.2f) + 6,
            Margin = margin ?? new Padding(0, 0, 0, 2)
        };
    }

    private static Image? TryLoadLogo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return Image.FromStream(fs);
        }
        catch
        {
            return null;
        }
    }

    private void BtnPrint_Click(object? sender, EventArgs e)
    {
        var printDoc = new PrintDocument();
        printDoc.PrintPage += PrintDoc_PrintPage;

        using var dialog = new PrintDialog { Document = printDoc, AllowSomePages = false, AllowSelection = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            printDoc.Print();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not print: {ex.Message}", "Print Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Mirrors the on-screen layout: centered logo/store header, then the
    // monospace itemized body, instead of just dumping lines of text.
    private void PrintDoc_PrintPage(object? sender, PrintPageEventArgs e)
    {
        var g = e.Graphics!;
        float centerX = e.MarginBounds.Left + e.MarginBounds.Width / 2f;
        float y = e.MarginBounds.Top;

        if (_logo != null)
        {
            var logoHeight = 60;
            var logoWidth = (int)(_logo.Width * (logoHeight / (float)_logo.Height));
            g.DrawImage(_logo, centerX - logoWidth / 2f, y, logoWidth, logoHeight);
            y += logoHeight + 8;
        }

        var nameFont = new Font("Segoe UI", 13f, FontStyle.Bold);
        y = DrawCentered(g, _storeName, nameFont, Brushes.Black, centerX, y);

        var smallFont = new Font("Segoe UI", 8.5f);
        if (!string.IsNullOrWhiteSpace(_address)) y = DrawCentered(g, _address, smallFont, Brushes.Gray, centerX, y);
        if (!string.IsNullOrWhiteSpace(_phone)) y = DrawCentered(g, _phone, smallFont, Brushes.Gray, centerX, y);

        y += 6;

        var bodyFont = new Font(FontFamily.GenericMonospace, 10);
        var lineHeight = bodyFont.GetHeight(g);
        foreach (var line in _itemizedBody.Replace("\r\n", "\n").Split('\n'))
        {
            if (y + lineHeight > e.MarginBounds.Bottom) break; // a receipt is one page
            g.DrawString(line, bodyFont, Brushes.Black, e.MarginBounds.Left, y);
            y += lineHeight;
        }

        y += 10;
        var footerFont = new Font("Segoe UI", 9f, FontStyle.Italic);
        DrawCentered(g, "Thank you for shopping with us!", footerFont, Brushes.Gray, centerX, y);
    }

    private static float DrawCentered(Graphics g, string text, Font font, Brush brush, float centerX, float y)
    {
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, centerX - size.Width / 2f, y);
        return y + size.Height;
    }
}
