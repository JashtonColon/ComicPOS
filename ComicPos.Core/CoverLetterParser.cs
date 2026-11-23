using System.Text.RegularExpressions;

namespace ComicPos.Core;

// Parses a cover letter out of a variant's free-text name/label ("Cover B
// Ryan Sook Variant" -> "B", "2nd Printing Cover C Variant" -> "C") -
// shared by every source that needs this (LocgService, MetronService),
// since the same ambiguity applies no matter which site/API the name
// came from: nothing guarantees a variant's name follows this pattern at
// all ("Virgin Variant", "Ryan Sook Variant" - an artist name with no
// letter anywhere in it). A null result means "couldn't tell" - callers
// treat that as a GUESS that still needs its own letter assigned from
// whatever's free (see AutoFetchService.ResolveCoverLetters), never as
// "must be Cover B."
public static class CoverLetterParser
{
    // Not anchored to the start of the string on purpose - confirmed
    // live that LOCG and Metron both put other text before "Cover X" in
    // some variant names ("2nd Printing Cover C Variant"), so an earlier
    // version of this regex anchored with "^" (LocgService's own,
    // now replaced by this shared copy) silently missed those and fell
    // back to a guess when a real letter was actually right there in
    // the text.
    private static readonly Regex Pattern = new(@"Cover\s+([A-Za-z])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? GuessFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var match = Pattern.Match(name);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}
