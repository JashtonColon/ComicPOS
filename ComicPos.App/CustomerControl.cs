using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.App;

public class CustomerControl : UserControl
{
    private readonly ICustomerService _customers;
    private readonly TextBox txtSearch = new();
    private readonly DataGridView grid = new();
    private readonly TextBox txtNewName = new();
    private readonly TextBox txtNewEmail = new();
    private readonly TextBox txtNewPhone = new();
    private readonly Button btnAdd = new();

    public CustomerControl(ICustomerService customers)
    {
        _customers = customers;

        Dock = DockStyle.Fill;

        txtSearch.Dock = DockStyle.Top;
        txtSearch.PlaceholderText = "Search customers...";
        txtSearch.TextChanged += (s, e) => Search();

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        var addPanel = new Panel { Dock = DockStyle.Bottom, Height = 35 };
        txtNewPhone.Dock = DockStyle.Right;
        txtNewPhone.Width = 120;
        txtNewPhone.PlaceholderText = "Phone";
        txtNewEmail.Dock = DockStyle.Right;
        txtNewEmail.Width = 160;
        txtNewEmail.PlaceholderText = "Email";
        txtNewName.Dock = DockStyle.Fill;
        txtNewName.PlaceholderText = "New customer name...";
        btnAdd.Dock = DockStyle.Right;
        btnAdd.Width = 80;
        btnAdd.Text = "Add";
        btnAdd.Click += AddCustomer_Click;

        addPanel.Controls.Add(txtNewName);
        addPanel.Controls.Add(txtNewEmail);
        addPanel.Controls.Add(txtNewPhone);
        addPanel.Controls.Add(btnAdd);

        Controls.Add(grid);
        Controls.Add(addPanel);
        Controls.Add(txtSearch);

        Search();
    }

    private void Search()
    {
        grid.DataSource = _customers.SearchCustomers(txtSearch.Text)
            .Select(c => new { c.Id, c.Name, c.Email, c.Phone })
            .ToList();
    }

    private void AddCustomer_Click(object? sender, EventArgs e)
    {
        var name = txtNewName.Text;
        var email = string.IsNullOrWhiteSpace(txtNewEmail.Text) ? null : txtNewEmail.Text;
        var phone = string.IsNullOrWhiteSpace(txtNewPhone.Text) ? null : txtNewPhone.Text;

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Name is required.", "Missing Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _customers.AddCustomer(name, email, phone);

        txtNewName.Clear();
        txtNewEmail.Clear();
        txtNewPhone.Clear();

        Search();
    }
}