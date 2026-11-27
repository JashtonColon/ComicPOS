using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

// Manages the staff list used by the register's "who's working" PIN picker.
// Deliberately simple - no permission levels, no password hashing (see
// Worker.Pin's own comment for why) - this exists purely so sales and
// receipts can say who rang them up.
public interface IWorkerService
{
    List<Worker> GetActiveWorkers();
    List<Worker> GetAllWorkers();
    Worker AddWorker(string name, string pin);
    void SetActive(int workerId, bool isActive);
    void UpdatePin(int workerId, string pin);

    // Returns the worker if workerId is active and pin matches, otherwise null.
    Worker? TryLogin(int workerId, string pin);

    // Added 2026-11-27 after the store's full database reset (see
    // full-reset-2026-11-27.md) wiped the Workers table along with
    // everything else, and immediately hit the exact edge case
    // MainLayout.razor's own GatedRoutes comment had already flagged and
    // dismissed as "not a concern for an already-running store": with
    // zero workers, Settings is gated (requires a logged-in worker) but
    // there's no way to log in, and the only way to add a worker IS from
    // Settings - a real chicken-and-egg lockout once the Workers table is
    // genuinely empty, not just "all inactive."
    //
    // Call once at startup (ComicPos.Web/Program.cs, right after
    // Database.Migrate()). Does nothing at all if even one Worker row
    // already exists (seeded or real) - so this only ever fires on a
    // brand-new install or right after a full reset, never on a normal
    // already-running store. Seeds one active worker ("Admin", PIN
    // "9876") using Worker's own ordinary Name/Pin fields - not a
    // separate hardcoded bypass mechanism - so it behaves exactly like
    // any other worker: visible and editable from Settings, its PIN can
    // be changed there, and the store can rename or deactivate it once
    // real workers are added.
    void EnsureDefaultWorkerExists();
}
