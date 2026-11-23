namespace ComicPos.Core.Models;

public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }

    public bool IsActive { get; set; } = true;

    public List<PullListEntry> PullListEntries { get; set; } = new();

    // This customer's store-credit ledger - see StoreCreditTransaction
    // for why the balance itself is never a column here. Loaded only
    // when a caller actually Includes it; ICustomerService.
    // GetStoreCreditBalance queries StoreCreditTransactions directly
    // rather than relying on this being populated.
    public List<StoreCreditTransaction> StoreCreditTransactions { get; set; } = new();
}