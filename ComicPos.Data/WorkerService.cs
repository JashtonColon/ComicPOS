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

        worker.Pin = pin.Trim();
        _db.SaveChanges();
    }

    public Worker? TryLogin(int workerId, string pin)
    {
        var worker = _db.Workers.Find(workerId);
        if (worker == null || !worker.IsActive) return null;

        return worker.Pin == pin.Trim() ? worker : null;
    }
}
