using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface ICustomerService
{
    Customer? FindById(int id);
    List<Customer> SearchCustomers(string text);
    Customer AddCustomer(string name, string? email, string? phone);
    void UpdateCustomer(Customer customer);
    void DeactivateCustomer(int id);
}