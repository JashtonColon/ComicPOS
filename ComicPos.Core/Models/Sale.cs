namespace ComicPos.Core.Models;

public class Sale
{
    public int Id { get; set; }

    public DateTime SoldAt { get; set; } = DateTime.Now;

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public bool IsVoided { get; set; } = false;

    // Store credit redeemed against this sale's total (0 = none used).
    // The actual ledger entry debiting the customer's balance lives in
    // StoreCreditTransaction (SaleId pointing back here) - this field is
    // just this sale's own record of how much of its total that covered,
    // so the receipt and VoidSale's refund-on-void logic don't need to
    // go searching the ledger for it.
    public decimal StoreCreditUsed { get; set; }

    public List<SaleLine> Lines { get; set; } = new();
}