using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface ICheckoutService
{
    // Starts a new, empty sale (not yet saved). customerId is optional,
    // matching Sale.CustomerId being int?.
    Sale StartSale(int? customerId = null);

    // Looks up the barcode, adds a line to the sale (or increases Quantity
    // if that cover is already in the cart). Returns false if the barcode
    // doesn't match any cover, so the caller (the checkout screen) can show
    // "not found" instead of crashing.
    bool ScanItem(Sale sale, string barcode, int quantity = 1);

    // Decrements stock for every line, then saves everything in one
    // transaction (per the SaveChanges discussion above).
    void CompleteSale(Sale sale);

    // Redeems up to `amount` of this sale's customer's store credit
    // balance toward it. Throws if the sale has no customer attached
    // (store credit is always tied to a specific customer's own ledger),
    // if amount is negative, or if amount exceeds that customer's
    // current balance - does NOT silently clamp down to the balance, so
    // a caller's UI can't accidentally apply less than what it told the
    // person it was applying. Does NOT check against the sale's own
    // total either (CheckoutService has no concept of tax at all - see
    // Checkout.razor's own GetTaxRate) - a caller that wants "never
    // apply more than what's owed" enforces that itself before calling
    // this, using whatever total it already computed. Only sets
    // sale.StoreCreditUsed on the in-memory sale; the actual ledger
    // debit is written by CompleteSale once the sale (and its real Id)
    // actually exist.
    void ApplyStoreCredit(Sale sale, decimal amount);


    List<string> GetLowStockWarnings(Sale sale);

    // Restores stock for every line and marks the sale voided. Throws if
    // the sale is already voided, so it can't be "un-voided" by accident
    // or double-restock the same stock twice.
    void VoidSale(int saleId);
}