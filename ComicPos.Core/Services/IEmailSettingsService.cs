using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IEmailSettingsService
{
    EmailSettings Load();
    void Save(EmailSettings settings);
}
