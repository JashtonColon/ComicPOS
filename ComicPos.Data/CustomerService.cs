using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class CustomerService : ICustomerService
{
    private readonly ComicDbContext _db;

    public CustomerService(ComicDbContext db)
    {
        _db = db;
    }

    public Customer? FindById(int id)
    {
        return _db.Customers.FirstOrDefault(c => c.Id == id);
    }

    public List<Customer> SearchCustomers(string text)
    {
        return _db.Customers
            .Where(c =>
                EF.Functions.Like(c.Name, $"%{text}%") ||
                (c.Email != null && EF.Functions.Like(c.Email, $"%{text}%")) ||
                (c.Phone != null && EF.Functions.Like(c.Phone, $"%{text}%")))
            .ToList();
    }
    public Customer AddCustomer(string name, string? email, string? phone)
    {
        var customer = new Customer
        {
            Name = name,
            Email = email,
            Phone = phone,
            IsActive = true
        };

        _db.Customers.Add(customer);
        _db.SaveChanges();

        return customer;
    }

    public void UpdateCustomer(Customer customer)
    {
        _db.Customers.Update(customer);
        _db.SaveChanges();
    }

    public void DeactivateCustomer(int id)
    {
        var customer = FindById(id);
        if (customer != null)
        {
            customer.IsActive = false;
            UpdateCustomer(customer);
        }
    }
}
