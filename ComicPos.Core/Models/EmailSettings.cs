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
}
