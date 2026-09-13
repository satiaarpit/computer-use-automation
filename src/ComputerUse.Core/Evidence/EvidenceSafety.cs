using System.Text.RegularExpressions;

namespace ComputerUse.Core.Evidence;

public sealed class EvidenceRedactor
{
    public const string RedactedValue = "[REDACTED]";

    private static readonly Regex EmailPattern = new(
        @"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex PhonePattern = new(
        @"(?<!\d)(?:\+?\d[\d .()-]{7,}\d)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex CredentialPattern = new(
        @"(?i)\b(?:bearer\s+|api[_-]?key\s*[:=]\s*|token\s*[:=]\s*)[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex OpaqueIdentifierPattern = new(
        @"(?<![A-Fa-f0-9])[A-Fa-f0-9]{24,}(?![A-Fa-f0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly string[] sensitiveValues;

    public EvidenceRedactor(IEnumerable<string>? sensitiveValues = null)
    {
        this.sensitiveValues = (sensitiveValues ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = value;
        foreach (var sensitiveValue in sensitiveValues)
        {
            redacted = redacted.Replace(sensitiveValue, RedactedValue, StringComparison.Ordinal);
        }

        redacted = CredentialPattern.Replace(redacted, RedactedValue);
        redacted = EmailPattern.Replace(redacted, RedactedValue);
        redacted = PhonePattern.Replace(redacted, RedactedValue);
        return OpaqueIdentifierPattern.Replace(redacted, RedactedValue);
    }
}

public static class EvidencePathPolicy
{
    public static string ResolveRoot(string evidenceRoot)
    {
        if (string.IsNullOrWhiteSpace(evidenceRoot))
        {
            throw new ArgumentException("An evidence root is required.", nameof(evidenceRoot));
        }

        if (evidenceRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || HasTraversalSegment(evidenceRoot))
        {
            throw new ArgumentException("The evidence root must not contain invalid or traversal segments.", nameof(evidenceRoot));
        }

        return Path.GetFullPath(evidenceRoot);
    }

    public static string ResolveFile(string evidenceRoot, string fileName)
    {
        var root = ResolveRoot(evidenceRoot);
        if (string.IsNullOrWhiteSpace(fileName) ||
            ContainsDirectorySeparator(fileName) ||
            fileName != Path.GetFileName(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Evidence file names must be simple valid file names.", nameof(fileName));
        }

        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Evidence files must remain under the approved evidence root.", nameof(fileName));
        }

        return candidate;
    }

    private static bool HasTraversalSegment(string path) => path
        .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
        .Any(segment => segment == "..");

    private static bool ContainsDirectorySeparator(string path) => path.IndexOfAny(['/', '\\']) >= 0;
}