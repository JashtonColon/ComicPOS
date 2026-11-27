using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.Data;

public class WorkerService : IWorkerService
{
    private readonly ComicDbContext _db;

    public WorkerService(ComicDbContext db)
    {
        _db = db;
    }

    public List<Worker> GetActiveWorkers() =>
        _db.Workers.Where(w => w.IsActive).OrderBy(w => w.Name).ToList();

    public List<Worker> GetAllWorkers() =>
        _db.Workers.OrderBy(w => w.Name).ToList();

    public Worker AddWorker(string name, string pin)
    {
        var trimmedPin = pin.Trim();

        // The PIN's only job is to confirm "this is really you" when picking
        // a name off the register's worker list (see Worker.Pin's comment) -
        // it only does that if the PIN actually picks out one worker. Two
        // active workers sharing a PIN would let whoever knows it log sales
        // under either name just by choosing the other one from the list,
        // so this is caught here rather than discovered later on a receipt.
        if (_db.Workers.Any(w => w.IsActive && w.Pin == trimmedPin))
            throw new InvalidOperationException("Another active worker already uses that PIN. Pick a different one.");

        var worker = new Worker { Name = name.Trim(), Pin = trimmedPin, IsActive = true };
        _db.Workers.Add(worker);
        _db.SaveChanges();
        return worker;
    }

    public void SetActive(int workerId, bool isActive)
    {
        var worker = _db.Workers.Find(workerId);
        if (worker == null) return;

        worker.IsActive = isActive;
        _db.SaveChanges();
    }

    public void UpdatePin(int workerId, string pin)
    {
        var worker = _db.Workers.Find(workerId);
        if (worker == null) return;

        var trimmedPin = pin.Trim();
        if (trimmedPin.Length == 0)
            throw new InvalidOperationException("Enter a PIN.");
        // Same one-PIN-one-worker rule as AddWorker, ignoring this worker's own row.
        if (_db.Workers.Any(w => w.Id != workerId && w.IsActive && w.Pin == trimmedPin))
            throw new InvalidOperationException("Another active worker already uses that PIN. Pick a different one.");

        worker.Pin = trimmedPin;
        _db.SaveChanges();
    }

    public Worker? TryLogin(int workerId, string pin)
    {
        var worker = _db.Workers.Find(workerId);
        if (worker == null || !worker.IsActive) return null;

        return worker.Pin == pin.Trim() ? worker : null;
    }

    // See IWorkerService's own doc comment for the full chicken-and-egg
    // story this fixes. Deliberately checks for ANY row at all (not just
    // active ones) - a store that has deliberately deactivated every
    // worker it has is a different, intentional situation and shouldn't
    // get a surprise worker re-seeded; a table with zero rows ever is
    // specifically "brand new install" or "just reset," which is the one
    // case this exists for.
    public void EnsureDefaultWorkerExists()
    {
        if (_db.Workers.Any()) return;

        _db.Workers.Add(new Worker { Name = "Admin", Pin = "9876", IsActive = true });
        _db.SaveChanges();
    }
}
