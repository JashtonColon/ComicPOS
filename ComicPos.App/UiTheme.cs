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
    public static readonly Color TextMuted = Color.FromArgb(105, 113, 122);
    public static readonly Color RowAlt = Color.FromArgb(245, 247, 250);
    public static readonly Color SelectionBack = Color.FromArgb(201, 222, 244);
    public static readonly Color NeutralButton = Color.FromArgb(230, 233, 237);
    public static readonly Color NeutralButtonHover = Color.FromArgb(216, 220, 225);
    public static readonly Color Danger = Color.FromArgb(173, 43, 43);

    public static readonly Font BaseFont = new("Segoe UI", 9.5f);
    public static readonly Font BoldFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font HeaderFont = new("Segoe UI", 14f, FontStyle.Bold);

    public static void StyleForm(Control root)
    {
        root.Font = BaseFont;
        root.BackColor = Background;
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
        b.FlatAppearance.BorderSize = 0;
        b.Cursor = Cursors.Hand;
        b.Font = primary ? BoldFont : BaseFont;

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

    public static void StyleTextBox(TextBox t)
    {
        t.Font = BaseFont;
        t.BorderStyle = BorderStyle.FixedSingle;
    }
}
