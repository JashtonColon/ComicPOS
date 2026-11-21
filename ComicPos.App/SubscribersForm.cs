using ComicPos.Core.Models;

namespace ComicPos.App;

// Read-only popup for "who's pulling this series" - the whole point is
// letting staff glance at it while physically sorting a shipment into
// customer folders, so it's just a plain list (name, preferred cover,
// quantity, notes, phone) with nothing to accidentally edit. Any real
// change to a subscription still happens from the Customers tab's own
// Pull List screen, which already owns that flow end to end.
public class SubscribersForm : Form
{
    public SubscribersForm(string seriesLabel, List<PullListEntry> entries)
    {
        Text = $"Who's Pulling {seriesLabel}";
        Width = 560;
        Height = 420;
        MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterParent;

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Customer", HeaderText = "Customer", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Cover", HeaderText = "Cover", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Qty", HeaderText = "Qty", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Phone", HeaderText = "Phone", ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Notes", HeaderText = "Notes", ReadOnly = true });

        foreach (var entry in entries.OrderBy(e => e.Customer.Name))
        {
            var rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["Customer"].Value = entry.Customer.Name;
            row.Cells["Cover"].Value = entry.PreferredCoverLetter;
            row.Cells["Qty"].Value = entry.Quantity;
            row.Cells["Phone"].Value = entry.Customer.Phone ?? "";
            row.Cells["Notes"].Value = entry.Notes ?? "";
        }

        var lblCount = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = entries.Count == 0
                ? "Nobody has this series on their pull list."
                : $"{entries.Count} customer(s) pulling this series — sort stock into these folders:"
        };

        var btnClose = new Button { Dock = DockStyle.Bottom, Height = 32, Text = "Close" };
        btnClose.Click += (s, e) => Close();

        Controls.Add(grid);
        Controls.Add(lblCount);
        Controls.Add(btnClose);

        UiTheme.StyleForm(this);
        UiTheme.StyleAll(this);
    }
}
