using ComicPos.Core.Models;

namespace ComicPos.Web.Services;

// Tracks who's currently "working the register" for one browser circuit.
// Registered Scoped (not Singleton) so each open tab/session has its own -
// logging in on the register up front doesn't change what a different
// open tab elsewhere in the store shows.
//
// Updated 2026-11-24: this USED to be purely attribution (see Worker.Pin's
// own comment - no permission levels, every worker could use every page
// regardless) - the store then asked for an actual lockout: Checkout,
// History, Customers, Inventory, Dashboard and FOC Report now require
// SOME worker to be logged in, and the register auto-locks after a
// minute of inactivity until someone logs back in (MainLayout gates
// those routes using IsLocked below; New Books/Import/Settings are left
// open - see MainLayout's own GatedRoutes comment). Still not a real
// permission boundary in the sense of different workers having different
// access - every worker can still do everything once logged in - this
// is just "someone has to be logged in and it can't sit open forever."
public class CurrentWorkerContext : IDisposable
{
    // How long the register can sit untouched before it locks and
    // requires a PIN again - a real shop register shouldn't stay usable
    // forever once nobody's actually standing at it.
    private static readonly TimeSpan LockoutAfter = TimeSpan.FromMinutes(1);

    // Checked on a timer rather than on every activity event - activity
    // is reported via NotifyActivity (see MainLayout's small JS watcher),
    // which just stamps a timestamp; this is what actually decides when
    // enough time has passed and flips IsLocked.
    private readonly Timer _inactivityTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public Worker? Current { get; private set; }
    public bool IsLocked { get; private set; }

    public event Action? Changed;

    public CurrentWorkerContext()
    {
        _inactivityTimer = new Timer(_ => CheckInactivity(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void SetCurrent(Worker? worker)
    {
        Current = worker;
        IsLocked = false;
        _lastActivityUtc = DateTime.UtcNow;
        Changed?.Invoke();
    }

    // Called from MainLayout's activity watcher whenever the person
    // actually does something on a gated page - moves the mouse, types,
    // clicks, scrolls. Keeps the lockout timer from firing while someone
    // is actively using the register. Does NOT itself clear IsLocked -
    // once locked, only a real PIN re-entry (SetCurrent) does that; this
    // just resets the clock so a currently-unlocked session doesn't lock
    // out from under someone mid-use.
    public void NotifyActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
    }

    // Runs on this context's own background timer (every 5s), NOT on the
    // Blazor renderer's thread - Changed's subscriber (MainLayout) always
    // marshals back via InvokeAsync(StateHasChanged), the same pattern
    // already used for a worker-switch, so that's safe to raise from
    // here.
    private void CheckInactivity()
    {
        if (Current == null || IsLocked) return;
        if (DateTime.UtcNow - _lastActivityUtc < LockoutAfter) return;

        IsLocked = true;
        Changed?.Invoke();
    }

    public void Dispose() => _inactivityTimer.Dispose();
}
