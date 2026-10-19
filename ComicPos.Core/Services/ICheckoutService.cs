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


    List<string> GetLowStockWarnings(Sale sale);
}