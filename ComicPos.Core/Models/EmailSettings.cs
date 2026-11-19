namespace ComicPos.Core.Models;

public class EmailSettings
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public bool UseSsl { get; set; } = true;
    public string FromEmail { get; set; } = "";
    public string FromDisplayName { get; set; } = "Comic POS";
    public string AdminEmail { get; set; } = "";

    // Opt-in: if true, the app emails the FOC report to AdminEmail on its
    // own once a day (see MainForm) instead of requiring someone to click
    // "Send Email to Admin" in the FOC Report tab every time. Off by
    // default - nobody should start getting automatic emails without
    // asking for it first.
    public bool AutoSendFocReport { get; set; } = false;
    public int FocReportDaysAhead { get; set; } = 14;
    public DateTime? LastFocReportSentAt { get; set; }
}
