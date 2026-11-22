using ComicPos.Core.Models;

namespace ComicPos.Web.Services;

// Tracks who's currently "working the register" for one browser circuit.
// Registered Scoped (not Singleton) so each open tab/session has its own -
// logging in on the register up front doesn't change what a different
// open tab elsewhere in the store shows. Not a security boundary (see
// Worker.Pin) - just attribution for receipts and the Dashboard.
public class CurrentWorkerContext
{
    public Worker? Current { get; private set; }

    public event Action? Changed;

    public void SetCurrent(Worker? worker)
    {
        Current = worker;
        Changed?.Invoke();
    }
}
