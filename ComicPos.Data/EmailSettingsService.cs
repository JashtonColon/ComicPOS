using System.Text.Json;
using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.Data;

// Stored as plain JSON in %LOCALAPPDATA%\ComicPos\email-settings.json.
// This is local-machine-only config, same folder as the database, and is
// not tracked in git (same .gitignore entry covers the whole ComicPos folder's
// data files). The SMTP password is NOT encrypted - fine for a single-user
// local app, but use an app password (Gmail, Outlook, etc.), not your main
// account password.
public class EmailSettingsService : IEmailSettingsService
{
    private readonly string _path;

    public EmailSettingsService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPos");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "email-settings.json");
    }

    public EmailSettings Load()
    {
        if (!File.Exists(_path)) return new EmailSettings();

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<EmailSettings>(json) ?? new EmailSettings();
    }

    public void Save(EmailSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
