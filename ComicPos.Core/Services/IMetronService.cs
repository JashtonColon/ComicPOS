using ComicPos.Core.Models;

namespace ComicPos.Core.Services;

// Metron (metron.cloud) - a second, independent community comic database,
// separate from both Comic Vine and GCD, with its own account and its own
// rate limit (confirmed on its own documentation: 20 requests/minute, 5,000
// requests/day for an authenticated account - there is no anonymous tier,
// unlike GCD, so this always needs a Settings-configured account to do
// anything at all). Tried as a THIRD fallback cover-art source for
// RescanExistingBooks, after both Comic Vine and GCD have already been
// asked and the issue is still missing a cover. A single call to its
// /issue/ list endpoint, filtered by series_name + number (+
// series_year_began when known), already returns a cover image URL
// directly in the list result - no separate detail call needed, unlike
// GCD's two-step list-then-detail dance.
public interface IMetronService
{
    // Never throws - see GcdCoverLookupResult's sibling, MetronCoverLookupResult,
    // for the same "always say why" contract.
    MetronCoverLookupResult FindAndDownloadCover(string seriesName, float issueNumber, int? startYear, string fileNameHint);
}

public class MetronCoverLookupResult
{
    public string? ImagePath { get; set; }

    // A short, FIXED category string (no interpolated specifics) so many
    // results can be grouped and counted together in a run's trace log.
    public string Reason { get; set; } = "";

    // Optional, per-call specific text (an exception message, a URL, a
    // snippet of an unexpected response) - useful as a handful of
    // concrete examples alongside the grouped counts.
    public string? Detail { get; set; }

    // Set when Metron itself returned 429, or when something (its own
    // bot-protection, seen returning an HTML challenge page instead of
    // JSON on metron.cloud's own website during testing) blocked the
    // request outright. Either way, further calls this run would almost
    // certainly fail the same way, so the caller should stop calling
    // Metron at all for the rest of the run instead of retrying it issue
    // by issue.
    public bool IsRateLimited { get; set; }

    public bool Success => !string.IsNullOrWhiteSpace(ImagePath);
}
