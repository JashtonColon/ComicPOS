using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IStoreSettingsService
{
    StoreSettings Load();
    void Save(StoreSettings settings);
}
