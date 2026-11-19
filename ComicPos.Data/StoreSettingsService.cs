using System.Text.Json;
using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.Data;

public class StoreSettingsService : IStoreSettingsService
{
    private readonly string _path;

    public StoreSettingsService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "store-settings.json");
    }

    public StoreSettings Load()
    {
        if (!File.Exists(_path)) return new StoreSettings();

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<StoreSettings>(json) ?? new StoreSettings();
    }

    public void Save(StoreSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
