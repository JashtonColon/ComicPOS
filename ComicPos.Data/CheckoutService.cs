using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class CheckoutService : ICheckoutService
{
    private readonly ComicDbContext _db;
    private readonly IInventoryService _inventory;
    private readonly ICustomerService _customers;

    public CheckoutService(ComicDbContext db, IInventoryService inventory, ICustomerService customers)
    {
        _db = db;
        _inventory = inventory;
        _customers = customers;
    }

    public void ApplyStoreCredit(Sale sale, decimal amount)
    {
        if (!sale.CustomerId.HasValue)
            throw new InvalidOperationException("Store credit can only be applied to a sale with a customer attached.");
        if (amount < 0)
            throw new InvalidOperationException("Store credit amount can't be negative.");

        var balance = _customers.GetStoreCreditBalance(sale.CustomerId.Value);
        if (amount > balance)
            throw new InvalidOperationException($"This customer only has {balance:C} in store credit available.");

        sale.StoreCreditUsed = amount;
    }

    public Sale StartSale(int? customerId = null)
    {
        return new Sale { CustomerId = customerId };
    }

    public bool ScanItem(Sale sale, string barcode, int quantity = 1)
    {
        var cover = _inventory.FindByBarcode(barcode);
        if (cover == null)
            return false;

        var existingLine = sale.Lines.FirstOrDefault(l => l.CoverId == cover.Id);
        if (existingLine != null)
        {
            existingLine.Quantity += quantity;
        }
        else
        {
            sale.Lines.Add(new SaleLine
            {
                Cover = cover,
                CoverId = cover.Id,
                Quantity = quantity,
                PriceEach = cover.Price
            });
        }

        return true;
    }

    public void CompleteSale(Sale sale)
    {
        // Deduct the full quantity sold, even past zero. Clamping this at 0
        // used to throw away the real oversell amount (stock=2, sell 5 ->
        // clamped to 0), which made VoidSale's symmetric "+= line.Quantity"
        // restore inflate stock on void (0 + 5 = 5 instead of back to 2).
        // Letting stock go negative keeps deduct/restore exact opposites of
        // each other and doubles as an honest "oversold by N" signal.
        foreach (var line in sale.Lines)
        {
            line.Cover.StockQuantity -= line.Quantity;
        }

        _db.Sales.Add(sale);
        try
        {
            _db.SaveChanges();
        }
        catch (Exception ex)
        {
            // Same cascading-failure risk documented on AutoFetchService.
            // TrySaveChanges, and sharper here: this DbContext is scoped
            // PER CIRCUIT in Blazor Server - one instance for a customer's
            // ENTIRE browsing session on the Checkout page, not just this
            // one click (see Program.cs's AddDbContext<ComicDbContext>
            // comment). Without clearing the tracker, a save failure here
            // would leave this half-applied sale tracked and poison every
            // later save this same browser tab makes for the rest of the
            // session - not just the next checkout attempt, but anything
            // else this circuit's services touch. Confirmed 2026-11-24:
            // this is also what was making the Low Stock Warning modal's
            // Yes/No/X buttons all look "broken" at once - CompleteSale
            // threw here uncaught, which crashed the whole Blazor Server
            // circuit (not just this one button), so EVERY button on the
            // page stopped responding until the page was reloaded. Now
            // caught and re-thrown as a plain message Checkout.razor's
            // FinishSale can show in _completeError instead.
            _db.ChangeTracker.Clear();
            throw new InvalidOperationException($"Couldn't save this sale: {ex.Message}", ex);
        }

        // The ledger debit is written AFTER the sale itself is saved, not
        // before - StoreCreditTransaction.SaleId wants the sale's real,
        // database-assigned Id, which doesn't exist until this
        // SaveChanges above runs. ApplyStoreCredit already validated the
        // amount against the customer's balance at the time it was
        // applied in the UI, but the balance is re-checked against
        // nothing here - between ApplyStoreCredit and CompleteSale
        // there's no further opportunity for it to have changed in this
        // single-checkout-screen app, so this just writes what was
        // already validated.
        if (sale.StoreCreditUsed > 0 && sale.CustomerId.HasValue)
        {
            _customers.AdjustStoreCredit(sale.CustomerId.Value, -sale.StoreCreditUsed, "Used on sale", sale.Id);
        }

        if (sale.CustomerId.HasValue)
            FulfillMatchingHolds(sale);
    }

    // The customer buying the exact issue + cover they had held IS the
    // pickup, so close those holds instead of leaving them "outstanding"
    // forever. Exact cover letter only: a hold for Cover A isn't picked up
    // by buying Cover B - staff can still close that one by hand from
    // Inventory. Oldest hold first; a hold is closed only once enough
    // copies were bought to cover its whole quantity.
    // shortcut: voiding the sale does not reopen these holds, add that to VoidSale if it comes up.
    private void FulfillMatchingHolds(Sale sale)
    {
        var customerId = sale.CustomerId!.Value;
        var changed = false;
        foreach (var group in sale.Lines.GroupBy(l => (l.Cover.IssueId, Letter: l.Cover.CoverLetter.Trim().ToUpperInvariant())))
        {
            var remaining = group.Sum(l => l.Quantity);
            var holds = _db.IssueHolds
                .Where(h => h.CustomerId == customerId && h.IssueId == group.Key.IssueId
                            && h.CoverLetter == group.Key.Letter && !h.IsFulfilled && !h.IsCancelled)
                .OrderBy(h => h.CreatedAt)
                .ToList();
            foreach (var hold in holds)
            {
                if (remaining < hold.Quantity) break;
                remaining -= hold.Quantity;
                hold.IsFulfilled = true;
                hold.FulfilledAt = DateTime.Now;
                changed = true;
            }
        }
        // The sale is already saved; a failure here must not look like a failed sale.
        if (!changed) return;
        try { _db.SaveChanges(); }
        catch { _db.ChangeTracker.Clear(); }
    }

    public List<string> GetLowStockWarnings(Sale sale)
    {
        var warnings = new List<string>();
        foreach (var line in sale.Lines)
        {
            if (line.Quantity > line.Cover.StockQuantity)
            {
                warnings.Add($"{line.Cover.Issue.Series.Name} #{line.Cover.Issue.IssueNumber} Cover {line.Cover.CoverLetter}: only {line.Cover.StockQuantity} in stock, selling {line.Quantity}");
            }
        }
        return warnings;
    }

    public void VoidSale(int saleId)
    {
        var sale = _db.Sales
            .Include(s => s.Lines)
            .ThenInclude(l => l.Cover)
            .FirstOrDefault(s => s.Id == saleId);

        if (sale == null)
            throw new InvalidOperationException($"Sale {saleId} not found.");

        if (sale.IsVoided)
            throw new InvalidOperationException("This sale is already voided.");

        foreach (var line in sale.Lines)
            line.Cover.StockQuantity += line.Quantity;

        sale.IsVoided = true;
        _db.SaveChanges();

        // Symmetric with CompleteSale's debit above - any store credit
        // this sale used comes back to the customer the same way stock
        // comes back to inventory, rather than being quietly lost.
        if (sale.StoreCreditUsed > 0 && sale.CustomerId.HasValue)
        {
            _customers.AdjustStoreCredit(sale.CustomerId.Value, sale.StoreCreditUsed, "Refunded from voided sale", sale.Id);
        }
    }
}