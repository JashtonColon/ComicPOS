namespace ComicPos.Core;

// Dependency-free image-dimension reader - added 2026-11-24 as part of the
// cover-image-pixelation fix (see AutoFetchService.UpgradeLowResolutionCovers
// and metron-integration-notes.md). Needs to tell whether an on-disk cover
// image is a low-resolution download (the "medium_url" root cause found in
// ComicVineService's two issue-fetch methods) without adding an image-
// processing NuGet package, which would need a dotnet restore against
// nuget.org that only the store's own machine can actually run. Both cover
// formats this app ever saves (PNG, JPEG) store their pixel dimensions in a
// small, fixed/well-documented header, readable directly from the file's own
// bytes - no decoding of the actual image data is needed.
public static class ImageDimensionReader
{
    public readonly record struct Dimensions(int Width, int Height);

    // Returns null (never throws) for a missing/unreadable file, a format
    // this doesn't recognize, or a truncated/corrupt header - every caller
    // treats "can't tell" the same as "leave this cover alone", never as
    // "this cover needs upgrading".
    public static Dimensions? TryReadDimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return TryReadDimensions(stream);
        }
        catch
        {
            return null;
        }
    }

    public static Dimensions? TryReadDimensions(Stream stream)
    {
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (ReadExact(stream, header) < 8) return null;

            // PNG signature: 89 50 4E 47 0D 0A 1A 0A
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return ReadPngDimensions(stream);

            // JPEG always starts with the SOI marker FF D8. header[2]/header[3]
            // are already the first two bytes of whatever marker follows SOI -
            // rather than seeking back, those two bytes are fed straight into
            // the same marker-scanning logic used for the rest of the file, so
            // this works even on a stream that can't seek.
            if (header[0] == 0xFF && header[1] == 0xD8)
                return ReadJpegDimensions(stream, header[2], header[3]);

            return null; // unrecognized format
        }
        catch
        {
            return null;
        }
    }

    // PNG: right after the 8-byte signature comes the IHDR chunk, which the
    // PNG spec requires to be first - a 4-byte length (always 13 for IHDR,
    // not relied on here), a 4-byte ASCII chunk type ("IHDR"), then the chunk
    // data: 4-byte big-endian width, 4-byte big-endian height, followed by
    // bit depth/color type/etc. (not needed here).
    private static Dimensions? ReadPngDimensions(Stream stream)
    {
        Span<byte> chunkHeader = stackalloc byte[8]; // 4-byte length + 4-byte type
        if (ReadExact(stream, chunkHeader) < 8) return null;

        var chunkType = System.Text.Encoding.ASCII.GetString(chunkHeader.Slice(4, 4));
        if (chunkType != "IHDR") return null; // malformed PNG - IHDR must come first

        Span<byte> dims = stackalloc byte[8]; // 4-byte width + 4-byte height, both big-endian
        if (ReadExact(stream, dims) < 8) return null;

        var width = ReadUInt32BigEndian(dims.Slice(0, 4));
        var height = ReadUInt32BigEndian(dims.Slice(4, 4));
        return new Dimensions((int)width, (int)height);
    }

    // JPEG is a stream of markers (FF xx). Most are followed by a 2-byte
    // big-endian length (INCLUDING those 2 length bytes themselves) and then
    // that many bytes of segment payload. The dimensions live in whichever
    // SOF (Start Of Frame) marker is present - markers C0-CF except C4/C8/CC,
    // which are DHT/JPG/DAC and not true SOF markers. A SOF segment's
    // payload is [1-byte precision][2-byte big-endian height][2-byte
    // big-endian width][...component data, not needed here]. Every other
    // marker is skipped forward by its own declared length instead of being
    // parsed.
    private static Dimensions? ReadJpegDimensions(Stream stream, byte firstLookaheadByte, byte secondLookaheadByte)
    {
        const int maxMarkersToScan = 200; // generous - real JPEGs rarely have more than a handful of segments before SOF
        byte? pending1 = firstLookaheadByte;
        byte? pending2 = secondLookaheadByte;

        // Allocated once outside the loop (CA2014: a stackalloc inside a loop
        // can build up stack usage across iterations instead of being freed
        // between them) and reused every iteration - both buffers are small,
        // fixed-size, and only ever read right after being filled.
        Span<byte> lenBytes = stackalloc byte[2];
        Span<byte> sofBody = stackalloc byte[5]; // precision(1) + height(2) + width(2)

        for (var i = 0; i < maxMarkersToScan; i++)
        {
            byte markerByte;
            if (pending1.HasValue && pending2.HasValue)
            {
                // The very first marker after SOI is identified from the two
                // bytes already read by the caller, without consuming
                // anything new from the stream for this one marker.
                var found = ResolveMarkerFromLookahead(stream, pending1.Value, pending2.Value, out markerByte);
                pending1 = null;
                pending2 = null;
                if (!found) return null;
            }
            else
            {
                var marker = FindNextMarker(stream);
                if (marker == null) return null; // ran out of file before finding a SOF
                markerByte = marker.Value;
            }

            // Markers with no payload at all - no length field follows.
            if (markerByte == 0xD8 || markerByte == 0xD9 || (markerByte >= 0xD0 && markerByte <= 0xD7))
                continue;

            if (ReadExact(stream, lenBytes) < 2) return null;
            var segmentLength = (lenBytes[0] << 8) | lenBytes[1];
            if (segmentLength < 2) return null; // malformed - length must include itself

            var isSof = markerByte >= 0xC0 && markerByte <= 0xCF &&
                        markerByte != 0xC4 && markerByte != 0xC8 && markerByte != 0xCC;

            if (isSof)
            {
                if (ReadExact(stream, sofBody) < 5) return null;
                var height = (sofBody[1] << 8) | sofBody[2];
                var width = (sofBody[3] << 8) | sofBody[4];
                return new Dimensions(width, height);
            }

            // Not a SOF - skip the rest of this segment's payload (already
            // consumed the 2 length bytes, so skip segmentLength - 2 more).
            var toSkip = segmentLength - 2;
            if (toSkip > 0) SkipBytes(stream, toSkip);
        }

        return null; // scanned a lot of markers without finding a SOF - give up rather than loop forever
    }

    // The two bytes already read right after SOI are normally FF xx (the
    // start of the first real marker, e.g. APP0) - but a run of 0xFF padding
    // bytes before the real marker type is valid too, so this re-plays the
    // same skip-padding logic FindNextMarker uses, just starting from bytes
    // already in hand instead of the stream.
    private static bool ResolveMarkerFromLookahead(Stream stream, byte b1, byte b2, out byte markerByte)
    {
        markerByte = 0;
        if (b1 != 0xFF) return false; // not a marker at all - malformed JPEG

        // Skip any extra 0xFF padding bytes (b2, then more read live if needed).
        var next = b2;
        while (next == 0xFF)
        {
            var read = stream.ReadByte();
            if (read == -1) return false;
            next = (byte)read;
        }
        if (next == 0x00) return false; // a stuffed byte right after SOI would be malformed

        markerByte = next;
        return true;
    }

    // Walks forward byte-by-byte looking for the next FF xx marker (xx != 00
    // and != FF - 0xFF00 is a stuffed byte inside entropy-coded data, and a
    // run of 0xFF padding bytes before a real marker is normal and must be
    // skipped past, not treated as the marker itself).
    private static byte? FindNextMarker(Stream stream)
    {
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            if (b != 0xFF) continue;

            int next;
            while ((next = stream.ReadByte()) == 0xFF) { } // skip padding 0xFF bytes
            if (next == -1) return null;
            if (next == 0x00) continue; // stuffed byte, not a real marker - keep scanning

            return (byte)next;
        }
        return null;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer.Slice(total));
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static void SkipBytes(Stream stream, int count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        Span<byte> scratch = stackalloc byte[Math.Min(count, 256)];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = Math.Min(remaining, scratch.Length);
            var read = stream.Read(scratch.Slice(0, toRead));
            if (read == 0) break;
            remaining -= read;
        }
    }
}
