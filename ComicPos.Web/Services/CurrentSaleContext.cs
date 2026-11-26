using ComicPos.Core.Models;

namespace ComicPos.Web.Services;

// Keeps the in-progress Checkout sale alive across page navigation within
// the same browser tab (added 2026-11-24 - "when im starting a checkout
// then go to antoehr page then go back to checkout i want to have the
// checkout and eerything there be saved"). Checkout.razor's own @code
// fields (_sale, _cashTendered, etc.) are PAGE-COMPONENT-scoped, not
// circuit-scoped - Blazor's router creates a brand-new instance of the
// page component (and re-runs OnInitialized) on every navigation TO it,
// even within the same still-connected circuit, which is what wiped the
// in-progress sale every time the clerk stepped away to another page and
// came back. This service is Scoped - one instance per circuit, same
// lifetime as CurrentWorkerContext and ComicDbContext itself (see
// Program.cs) - so it survives exactly as long as the browser tab's
// SignalR connection does, independent of which page is currently
// showing. The in-memory Sale object itself (and the Cover/Issue/Series
// entities its lines reference) stay perfectly valid the whole time too,
// since nothing about navigating between pages disposes or recycles the
// circuit's own DbContext - only a real circuit teardown (tab closed,
// reloaded, or disconnected) would do that, which is a fresh start
// either way.
//
// Only the Sale itself plus the couple of fields genuinely needed to
// resume editing it are kept here - everything else Checkout.razor has
// (the barcode scan box, scan errors, customer search results, the
// low-stock/prior-purchase confirmation popups, the just-completed
// receipt) is transient UI state that's fine - arguably better - to
// reset on a fresh visit to the page rather than resurrected stale.
public class CurrentSaleContext
{
    public Sale? Sale { get; private set; }
    public string? SelectedCustomerName { get; private set; }
    public string CashTendered { get; set; } = "";
    public string StoreCreditToApplyInput { get; set; } = "";

    public void SetSale(Sale sale, string? selectedCustomerName)
    {
        Sale = sale;
        SelectedCustomerName = selectedCustomerName;
    }

    public void SetCustomer(string? selectedCustomerName) => SelectedCustomerName = selectedCustomerName;

    // Called once a sale actually completes - clears everything so the
    // NEXT visit to Checkout starts a genuinely new sale instead of
    // resuming the one that just finished.
    public void Clear()
    {
        Sale = null;
        SelectedCustomerName = null;
        CashTendered = "";
        StoreCreditToApplyInput = "";
    }
}
