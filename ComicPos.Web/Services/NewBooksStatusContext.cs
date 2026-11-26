namespace ComicPos.Web.Services;

// Keeps New Books' result/status banner (the green "Done - ..." message
// and its log-trace box under it) alive across page navigation within the
// same browser tab (added 2026-11-24 - "the alerst goes away whern you
// click off new books. i want the alert to show back up when you go back
// to new books"). Same root cause and same fix pattern as
// CurrentSaleContext on Checkout: NewBooks.razor's own _resultMessage/
// _resultTrace @code fields are PAGE-COMPONENT-scoped, not circuit-scoped
// - Blazor's router creates a brand-new instance of the page (and resets
// every field to its default) on every navigation TO it, even within the
// same still-connected circuit, which is what made the banner from the
// last "Check Now"/import/backfill run disappear the moment you left the
// page.
//
// This is deliberately just plain storage, not a CurrentWorkerContext-
// style service with a Changed event - NewBooks.razor is the only
// component that ever reads or writes these two values (unlike
// CurrentWorkerContext, which MainLayout, WorkerGateOverlay, and every
// gated page all need to react to), so there's no other component that
// needs telling when they change.
public class NewBooksStatusContext
{
    public string? ResultMessage { get; set; }
    public string? ResultTrace { get; set; }
}
