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
}
