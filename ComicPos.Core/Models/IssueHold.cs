namespace ComicPos.Core.Models;

// A one-time hold on a specific issue/cover for a customer, with no
// ongoing subscription attached - distinct from PullListEntry, which is
// always a standing, recurring subscription to a whole SERIES (every new
// issue going forward). This is for "just this one, this one time" -
// a back-issue a customer asked to be set aside, a convention exclusive,
// a gift, a single cover variant someone wants without signing up for
// the series itself.
public class IssueHold
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public int IssueId { get; set; }
    public Issue Issue { get; set; } = null!;

    // Which specific cover variant, same convention as PullListEntry -
    // defaults to "A" when the customer doesn't care which printing.
    public string CoverLetter { get; set; } = "A";

    public int Quantity { get; set; } = 1;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Set once the customer actually picks it up - keeps the hold in
    // history (same soft-delete spirit as PullListEntry.IsActive/
    // Customer.IsActive) instead of deleting the row, so there's a record
    // of what was held and picked up versus held and never claimed.
    public bool IsFulfilled { get; set; }
    public DateTime? FulfilledAt { get; set; }

    // The sale that picked this hold up, when checkout closed it
    // automatically (CheckoutService.FulfillMatchingHolds). Lets VoidSale
    // reopen exactly those holds. Null for holds closed by hand.
    public int? FulfilledBySaleId { get; set; }

    // A hold that's no longer wanted (customer changed their mind) -
    // separate from IsFulfilled so "cancelled" and "picked up" aren't the
    // same bucket when looking back at history.
    public bool IsCancelled { get; set; }
}
