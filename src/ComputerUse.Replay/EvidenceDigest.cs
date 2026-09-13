using System.Security.Cryptography;
using System.Text;

namespace ComputerUse.Replay;

public static class EvidenceDigest
{
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> CanonicalTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json",
        ".jsonl",
        ".txt"
    };

    public static async Task<string> Sha256FileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!CanonicalTextExtensions.Contains(Path.GetExtension(path)))
        {
            await using var stream = File.OpenRead(path);
            return Hex(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var offset = HasUtf8Preamble(bytes) ? Utf8Preamble.Length : 0;
        var content = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        var canonicalContent = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Hex(SHA256.HashData(StrictUtf8.GetBytes(canonicalContent)));
    }

    private static bool HasUtf8Preamble(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Utf8Preamble);

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
