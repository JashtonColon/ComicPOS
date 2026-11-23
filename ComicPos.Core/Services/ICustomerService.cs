using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface ICustomerService
{
    Customer? FindById(int id);
    List<Customer> SearchCustomers(string text);
    Customer AddCustomer(string name, string? email, string? phone);
    void UpdateCustomer(Customer customer);
    void DeactivateCustomer(int id);

    // The customer's current store credit balance - always the SUM of
    // their StoreCreditTransactions ledger rows (see that model's own
    // doc comment for why there's no separate mutable balance column to
    // read instead). Zero for a customer with no ledger rows at all, not
    // an error.
    decimal GetStoreCreditBalance(int customerId);

    // Full ledger history for the Customers page's credit panel, newest
    // first.
    List<StoreCreditTransaction> GetStoreCreditHistory(int customerId);

    // Writes one ledger row and returns it. amount is signed - positive
    // adds credit (a manual top-up, a refund issued as credit), negative
    // spends it (checkout redemption, see ICheckoutService.
    // ApplyStoreCredit). saleId links this entry back to the sale that
    // caused it (redeemed on, or refunded from a voided one) - null for
    // a plain manual adjustment. Does NOT stop a negative amount from
    // taking the balance below zero on its own - callers that need that
    // guarantee (checkout) check the current balance first and decide
    // what to allow; a manual admin adjustment is deliberately allowed
    // to go negative (e.g. correcting an earlier mistaken top-up).
    StoreCreditTransaction AdjustStoreCredit(int customerId, decimal amount, string reason, int? saleId = null);
}