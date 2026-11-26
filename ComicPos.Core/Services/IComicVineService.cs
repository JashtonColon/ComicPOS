namespace ComicPos.Core.Services;

// A Comic Vine "volume" is what they call a series. Search results are
// intentionally ambiguous-friendly - several volumes can share a name
// (reboots, different publishers), so the store picks the right one once
// per series rather than the app guessing.
public class ComicVineVolumeMatch
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? StartYear { get; set; }
    public string Publisher { get; set; } = string.Empty;
    public int IssueCount { get; set; }
}

public class ComicVineNewIssue
{
    public float IssueNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly? StoreDate { get; set; }
    public string? ImageUrl { get; set; }

    // Which Comic Vine volume this issue belongs to - populated when the
    // issue was found by a search that isn't already scoped to one volume
    // (e.g. the new-#1s discovery pass), so the caller can create or match
    // a Series for it.
    public int? VolumeId { get; set; }
    public string VolumeName { get; set; } = string.Empty;
}

public class ComicVineVolumeDetails
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? StartYear { get; set; }
    public string Publisher { get; set; } = string.Empty;
}

public interface IComicVineService
{
    // Searches Comic Vine's volumes by name so the store can confirm which
    // one is theirs before linking a series to it.
    List<ComicVineVolumeMatch> SearchVolumes(string seriesName);

    // Issues for a volume with a store date in [sinceStoreDate, untilStoreDate]
    // (comic vine doesn't track solicitation/FOC dates, only street dates -
    // FOC is estimated as roughly 3 weeks before street date by the caller).
    List<ComicVineNewIssue> GetIssuesInRange(int volumeId, DateOnly sinceStoreDate, DateOnly untilStoreDate);

    // Every #1 issue (any volume, any publisher) with a store date in range -
    // this is how brand-new series launches are discovered, since a series
    // nobody carries yet has no local Series row to match against. Each
    // result's VolumeId/VolumeName is set so the caller can create a Series.
    List<ComicVineNewIssue> GetNewNumberOneIssues(DateOnly sinceStoreDate, DateOnly untilStoreDate);

    // Looks up a volume directly (by the VolumeId from GetNewNumberOneIssues)
    // to get its publisher and start year before deciding whether to add it.
    ComicVineVolumeDetails? GetVolumeDetails(int volumeId);

    // The most recently-active volumes from one named publisher (sorted by
    // Comic Vine's own "last updated" activity, not by start year - a
    // long-running ongoing title like Batman or Amazing Spider-Man updates
    // constantly as new issues get catalogued, so it shows up here just as
    // readily as a brand-new launch). This is how "everything a tracked
    // publisher is currently putting out" is found, without scanning every
    // issue on Comic Vine (which is mostly noise from publishers a US
    // comic shop doesn't carry). Returns an empty list if the publisher
    // name doesn't resolve to a Comic Vine publisher.
    List<ComicVineVolumeMatch> GetActiveVolumesByPublisher(string publisherName, int limit);

    // Downloads a cover image to a local cache folder and returns the
    // saved file's full path, or null if the download failed.
    string? DownloadCoverImage(string imageUrl, string fileNameHint);

    // Same destination folder/naming convention as DownloadCoverImage,
    // but for image bytes the caller already has in hand - no network
    // fetch at all. Added for AutoFetchService.ImportLunarDistributionRelease,
    // whose source (a distributor's own Excel release file) embeds the
    // real cover photo directly in the workbook rather than linking to
    // one. Returns the saved file's full path, or throws on a genuine
    // I/O failure (unlike DownloadCoverImage's "return null" on failure -
    // there's no network flakiness to shrug off here, so a failure here
    // is always worth surfacing as a real error to the caller).
    string SaveCoverImageBytes(byte[] imageBytes, string fileNameHint, string extension = ".jpg");

    // Added 2026-11-24 for the cover-image-pixelation fix (see
    // AutoFetchService.UpgradeLowResolutionCovers) - a conservative,
    // exact-match-only lookup of ONE issue's best available image URL,
    // for retroactively upgrading a cover that was already saved at Comic
    // Vine's smaller "medium_url" size (before GetIssuesInRange/
    // GetNewNumberOneIssues were fixed to prefer "super_url" for every
    // NEW download). Deliberately narrow: looks up the given volume's
    // issues filtered to this exact issue number, and only returns a URL
    // when exactly one issue matches - never guesses between several
    // candidates, and never falls back to a fuzzy name search. Returns
    // null (not an exception) for "no exact match" or "matched but has no
    // image at all" - both mean "leave whatever cover is already on file
    // alone", same spirit as DownloadCoverImage's null-on-failure.
    string? FindBestCoverImageUrl(int volumeId, float issueNumber);
}
