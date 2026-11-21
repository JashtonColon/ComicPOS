namespace ComicPos.App;

// One place for the whole app's look. Every screen calls UiTheme.StyleAll(this)
// once at the end of its constructor, which walks its own controls (including
// nested panels/split containers) and applies consistent colors/fonts, locks
// down DataGridView column/row resizing and SplitContainer splitters (nothing
// in the app should feel "draggable" to a cashier), and gives buttons a flat,
// modern look. Mark a button as the main action on its screen by setting
// button.Tag = "primary" before calling StyleAll - everything else gets the
// neutral button style automatically.
public static class UiTheme
{
    public static readonly Color PrimaryDark = Color.FromArgb(30, 55, 90);
    public static readonly Color PrimaryAccent = Color.FromArgb(41, 98, 163);
    public static readonly Color PrimaryAccentHover = Color.FromArgb(30, 78, 135);
    public static readonly Color Background = Color.FromArgb(241, 243, 246);
    public static readonly Color PanelBackground = Color.White;
    public static readonly Color BorderColor = Color.FromArgb(221, 226, 231);
    public static readonly Color TextPrimary = Color.FromArgb(32, 36, 41);
    public static readonly Color TextMuted = Color.FromArgb(85, 92, 99); // ~6.8:1 contrast on white - was ~4.95:1, uncomfortably close to the WCAG AA floor
    public static readonly Color RowAlt = Color.FromArgb(245, 247, 250);
    public static readonly Color SelectionBack = Color.FromArgb(201, 222, 244);
    public static readonly Color NeutralButton = Color.FromArgb(230, 233, 237);
    public static readonly Color NeutralButtonHover = Color.FromArgb(216, 220, 225);
    public static readonly Color Danger = Color.FromArgb(173, 43, 43);
    // Noticeably darker than NeutralButton (230,233,237) - that color
    // alone against BorderColor (221,226,231) was too close in value to
    // read as an outline, so neutral buttons had no visible edge at all.
    public static readonly Color ButtonBorder = Color.FromArgb(198, 204, 211);

    public static readonly Font BaseFont = new("Segoe UI", 9.5f);
    public static readonly Font BoldFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font HeaderFont = new("Segoe UI", 14f, FontStyle.Bold);

    // Several dialog footers (Save/Cancel, Hold It/Cancel, etc.) never set
    // an explicit Button.Height at all and were defaulting to WinForms'
    // stock ~23px button size - cramped and easy to miss-click. This is a
    // floor, not a fixed size: Math.Max in StyleButton leaves every
    // already-taller button (the 30-34px toolbar buttons elsewhere) alone,
    // and is a no-op for any Dock=Fill button since Dock overrides it.
    private const int MinButtonHeight = 34;

    public static void StyleForm(Control root)
    {
        root.Font = BaseFont;
        root.BackColor = Background;
        EnableDoubleBuffering(root);
    }

    // DoubleBuffered is `protected` on Control, so this static helper
    // class can't set it directly even though every screen calls
    // StyleForm with its own `this`. Reflection is the standard way
    // around that. Turning it on everywhere kills a whole class of
    // WinForms redraw glitch - a stale rectangle or ghost region left on
    // screen until some unrelated repaint comes along - that can
    // otherwise show up whenever a child control's content changes
    // rapidly (e.g. a listbox repopulating on every keystroke) inside a
    // more complex Dock layout, such as the blank overlay that appeared
    // above the Pull List screen's search box while typing. Safe and a
    // no-op for any control that already double-buffers or doesn't
    // benefit from it.
    private static readonly System.Reflection.PropertyInfo? DoubleBufferedProp =
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static void EnableDoubleBuffering(Control root)
    {
        try { DoubleBufferedProp?.SetValue(root, true); } catch { /* best-effort */ }

        foreach (Control c in root.Controls)
            EnableDoubleBuffering(c);
    }

    // Call once per screen, after all child controls have been added.
    public static void StyleAll(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case DataGridView grid:
                    StyleGrid(grid);
                    break;
                case Button btn:
                    StyleButton(btn, (btn.Tag as string) == "primary");
                    break;
                case TextBox { Multiline: false } tb:
                    StyleTextBox(tb);
                    break;
                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.Font = BaseFont;
                    break;
                case ListBox lb:
                    lb.Font = BaseFont;
                    lb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case DateTimePicker dtp:
                    dtp.Font = BaseFont;
                    break;
                case SplitContainer split:
                    split.IsSplitterFixed = true; // no draggable splitter bars
                    split.BackColor = BorderColor;
                    split.Panel1.BackColor = PanelBackground;
                    split.Panel2.BackColor = PanelBackground;
                    break;
            }

            if (c.HasChildren)
                StyleAll(c);
        }
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = PanelBackground;
        grid.GridColor = BorderColor;
        grid.Font = BaseFont;
        grid.RowHeadersVisible = false;

        // DataGridView's own cell tooltips default to on, and show the
        // full cell text whenever a column is too narrow to display it.
        // Every grid in this app already shows exactly what it needs to
        // (no column relies on truncation+hover to be readable), and the
        // built-in tooltip has a known WinForms quirk: redrawing a grid's
        // rows (Rows.Clear()+Rows.Add() during a refresh) while the mouse
        // sits still over the old row position can leave its last tooltip
        // window stuck on screen, detached from any real cell, until the
        // mouse moves again - the "floating number" that sometimes
        // appears over the New Books grid. Turning it off here kills that
        // whole bug class everywhere in the app, not just in one screen.
        grid.ShowCellToolTips = false;

        // Size each column to fit its own content instead of stretching to
        // fill the grid's full width - a lone text column no longer balloons
        // out to eat the whole window, leaving the rest of the row blank.
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;

        // Nothing in a POS grid should be draggable by a cashier.
        grid.AllowUserToResizeColumns = false;
        grid.AllowUserToResizeRows = false;
        grid.AllowUserToOrderColumns = false;

        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 34;
        grid.RowTemplate.Height = 28;

        grid.ColumnHeadersDefaultCellStyle.BackColor = PrimaryDark;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Font = BoldFont;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = PrimaryDark;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);

        grid.DefaultCellStyle.SelectionBackColor = SelectionBack;
        grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
        grid.DefaultCellStyle.Padding = new Padding(6, 2, 6, 2);
        grid.DefaultCellStyle.Font = BaseFont;
        grid.AlternatingRowsDefaultCellStyle.BackColor = RowAlt;
    }

    public static void StyleButton(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        b.Font = primary ? BoldFont : BaseFont;
        // Compact width, taller height - more vertical padding than
        // horizontal, so a button reads as a rounded tile rather than a
        // long bar. AutoSize+GrowOnly (below) is what actually keeps text
        // from clipping ("Cancel" rendering as "Canc" when the fixed
        // Width=90 some dialogs set wasn't enough room once padding ate
        // into it) - it only ever grows a button past whatever Width/Height
        // the screen that built it already set, never shrinks it.
        b.Padding = new Padding(8, 6, 8, 6);
        if (b.Height < MinButtonHeight) b.Height = MinButtonHeight;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowOnly;

        if (primary)
        {
            b.FlatAppearance.BorderSize = 0;
        }
        else
        {
            // Primary buttons already pop via the solid accent fill;
            // neutral ones were a flat gray slab with no edge at all
            // against the (very similar gray) panel behind them.
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = ButtonBorder;
        }

        ApplyRoundedCorners(b);

        // The button's own preferred size just changed (padding, the
        // height floor) - for a button that was already sitting in a
        // FlowLayoutPanel laid out BEFORE this ran (every button on every
        // screen, since StyleAll runs last), that resize wasn't reliably
        // reflowing its siblings, so each button grew in place and
        // overlapped the next one instead of pushing it over. Forcing the
        // parent to re-layout right now, with the final size already set,
        // is what actually fixes that instead of hoping WinForms notices.
        b.Parent?.PerformLayout();

        if (primary)
        {
            b.BackColor = PrimaryAccent;
            b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = PrimaryAccentHover;
            b.FlatAppearance.MouseDownBackColor = PrimaryDark;
        }
        else
        {
            b.BackColor = NeutralButton;
            b.ForeColor = TextPrimary;
            b.FlatAppearance.MouseOverBackColor = NeutralButtonHover;
            b.FlatAppearance.MouseDownBackColor = BorderColor;
        }
    }

    // Clips the button to a rounded-rect Region instead of its natural
    // square corners - re-applied on every resize since AutoSize (set in
    // StyleButton above) changes a button's size after this first runs,
    // and a Region computed against the old size would clip the new one
    // wrong (corners cut square on one side, or clipped off entirely).
    private const int ButtonCornerRadius = 10;

    private static void ApplyRoundedCorners(Button b)
    {
        void SetRegion()
        {
            if (b.Width <= 0 || b.Height <= 0) return;
            var d = Math.Min(ButtonCornerRadius * 2, Math.Min(b.Width, b.Height));
            var rect = new Rectangle(0, 0, b.Width, b.Height);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            b.Region?.Dispose();
            b.Region = new Region(path);
        }
        SetRegion();
        b.Resize += (s, e) => SetRegion();
    }

    public static void StyleTextBox(TextBox t)
    {
        t.Font = BaseFont;
        t.BorderStyle = BorderStyle.FixedSingle;
    }

    // A plain, intentional-looking "no cover yet" tile instead of
    // DataGridView/PictureBox's default broken-image red X - used for
    // anything that genuinely has no cover art on file yet (most
    // collected editions/TPs/HCs: Comic Vine doesn't catalog those at
    // all, so there's no art to backfill for them). Built fresh per call
    // rather than cached, since callers want different sizes (a small
    // grid thumbnail vs. a larger detail-popup tile) - this only runs
    // once per row redraw, not in a tight loop, so that's not worth
    // optimizing away.
    public static Bitmap BuildNoCoverPlaceholder(int width, int height)
    {
        var bmp = new Bitmap(Math.Max(width, 1), Math.Max(height, 1));
        using var g = Graphics.FromImage(bmp);
        g.Clear(RowAlt);
        using (var pen = new Pen(BorderColor))
            g.DrawRectangle(pen, 0, 0, bmp.Width - 1, bmp.Height - 1);
        using var font = new Font("Segoe UI", 6.5f);
        using var brush = new SolidBrush(TextMuted);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("No\nCover", font, brush, new RectangleF(0, 0, bmp.Width, bmp.Height), format);
        return bmp;
    }

    // "Let me see that bigger" for a cover thumbnail - used by every
    // PictureBox that shows cover art (New Book detail cards, the
    // Inventory cover preview). Click-anywhere-or-Escape to close rather
    // than a close button, since this is meant to be a quick peek, not
    // another dialog to manage. Does not take ownership of the image (no
    // Dispose here) since callers keep showing it in their own PictureBox
    // after this closes.
    public static void ShowImageZoom(IWin32Window? owner, Image image, string title)
    {
        using var zoomForm = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            BackColor = Color.Black,
            ShowIcon = false
        };

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        var maxW = (int)(screen.Width * 0.8);
        var maxH = (int)(screen.Height * 0.85);
        zoomForm.ClientSize = new Size(
            Math.Max(Math.Min(image.Width + 40, maxW), 320),
            Math.Max(Math.Min(image.Height + 40, maxH), 400));

        var pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = image,
            BackColor = Color.Black,
            Cursor = Cursors.Hand
        };
        pic.Click += (s, e) => zoomForm.Close();
        zoomForm.KeyPreview = true;
        zoomForm.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) zoomForm.Close(); };

        zoomForm.Controls.Add(pic);
        zoomForm.ShowDialog(owner);
    }
}
