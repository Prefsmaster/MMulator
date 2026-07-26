using System.IO.Compression;

namespace P2000.Machine.State;

/// <summary>
/// Gzip helper for the embedded media blobs <c>.state</c> device blocks carry (project CLAUDE.md
/// milestones 20/20a; reference doc §3a "RESOLVED — mounted media CONTENT travels inside
/// <c>.state</c>"). Both raw disk sector dumps and compact <c>.cas</c>-format bytes compress well
/// (large runs of unformatted/blank space, repetitive framing) — this is the default applied to
/// both, not a per-device decision.
/// </summary>
internal static class StateBlobCompression
{
    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    public static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
