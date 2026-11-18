using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

public interface IEmailService
{
    void SendFocReport(List<FocReportEntry> entries, EmailSettings settings);
}
