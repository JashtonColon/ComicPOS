namespace ComicPos.Core.Models;

// A staff member who can "clock in" at the register via a quick PIN picker
// (not a security login - see Pin below). Purely for attribution: which
// worker rang up a given sale, shown on receipts and reports.
public class Worker
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // A short PIN (e.g. 4 digits) typed to confirm "this is really you" when
    // switching who's working the register. Deliberately NOT a real password
    // or a permission boundary - every worker can still use every page the
    // same as before. Stored as entered so an admin can see/reset it from
    // Settings without a "forgot PIN" flow.
    public string Pin { get; set; } = "";

    public bool IsActive { get; set; } = true;
}
