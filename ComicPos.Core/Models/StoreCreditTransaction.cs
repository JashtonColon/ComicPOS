namespace ComicPos.Core.Models;

// One entry in a customer's store-credit ledger. The customer's current
// balance is never stored as its own mutable column anywhere - it's
// always the SUM of these rows for that customer (see
// ICustomerService.GetStoreCreditBalance) so it can never drift out of
// sync with its own history, and every change to a customer's balance
// has a permanent, inspectable reason attached to it. This is real
// money (or the store's equivalent of it), so an auditable trail beats
// a faster-but-silent running total.
public class StoreCreditTransaction
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    // Positive = credit added to the customer's balance (a manual top-up,
    // a refund issued as credit instead of cash). Negative = credit spent
    // (redeemed at checkout against a sale's total).
    public decimal Amount { get; set; }

    // Short human-readable reason - "Store credit issued", "Refund for
    // damaged item", "Used on sale #123", "Manual adjustment" - shown in
    // the customer's credit history on the Customers page.
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Set only when this transaction is tied to a specific checkout -
    // either credit redeemed on that sale (negative Amount) or credit
    // refunded back because that sale was later voided (positive Amount).
    // Null for a manual admin adjustment that isn't tied to any sale.
    public int? SaleId { get; set; }
    public Sale? Sale { get; set; }
}
