using System.Net;
using System.Net.Mail;
using System.Text;
using ComicPos.Core.Models;
using ComicPos.Core.Services;

namespace ComicPos.Data;

public class EmailService : IEmailService
{
    public void SendFocReport(List<FocReportEntry> entries, EmailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.AdminEmail))
            throw new InvalidOperationException("Email settings are not configured. Set them in the Settings tab first.");

        var body = BuildBody(entries);

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromEmail, settings.FromDisplayName),
            Subject = $"Pull List - Upcoming FOC Report ({DateTime.Now:MMM d, yyyy})",
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(settings.AdminEmail);

        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
        {
            EnableSsl = settings.UseSsl,
            Credentials = new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword)
        };

        client.Send(message);
    }

    public static string BuildBody(List<FocReportEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Upcoming FOC - Pull List Buy Report");
        sb.AppendLine("====================================");

        if (entries.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Nothing with subscribers coming up in this window.");
            return sb.ToString();
        }

        DateOnly? lastDate = null;
        foreach (var entry in entries)
        {
            if (entry.FocDate != lastDate)
            {
                sb.AppendLine();
                sb.AppendLine($"FOC {entry.FocDate:MM/dd/yyyy}");
                sb.AppendLine(new string('-', 30));
                lastDate = entry.FocDate;
            }

            sb.AppendLine($"{entry.SeriesName} ({entry.StartYear}) #{entry.IssueNumber:0.##} - {entry.TotalQuantity} to buy");

            // Compact per-cover breakdown - this is the part meant to be
            // read in a glance before placing the order ("2x A, 1x B"),
            // not just a raw subscriber count.
            var coverTotals = entry.Subscribers
                .GroupBy(s => s.PreferredCover)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Sum(s => s.Quantity)}x {g.Key}");
            sb.AppendLine($"    Covers needed: {string.Join(", ", coverTotals)}");

            foreach (var sub in entry.Subscribers)
            {
                var qtyPart = sub.Quantity > 1 ? $" x{sub.Quantity}" : "";
                var flag = sub.CoverIsAvailable ? "" : "  *** cover not on file for this printing - check with customer ***";
                sb.AppendLine($"    - {sub.CustomerName} [Cover {sub.PreferredCover}]{qtyPart}{flag}");
            }
        }

        return sb.ToString();
    }
}
