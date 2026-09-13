using ComputerUse.Core.Evidence;

namespace ComputerUse.Core.Tests;

public sealed class EvidenceSafetyTests
{
    [Fact]
    public void Redactor_removes_configured_secrets_and_common_sensitive_patterns()
    {
        const string canary = "canary-secret-92841";
        var redactor = new EvidenceRedactor([canary]);

        const string sessionId = "e046232383ec44dea629ae2626da257a";
        var result = redactor.Redact($"secret={canary}; email person@example.test; phone +1 (555) 010-1234; Authorization: Bearer token-value; session {sessionId}");

        Assert.DoesNotContain(canary, result, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.test", result, StringComparison.Ordinal);
        Assert.DoesNotContain("555", result, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", result, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionId, result, StringComparison.Ordinal);
        Assert.Contains(EvidenceRedactor.RedactedValue, result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("safe/../../escape")]
    [InlineData("safe\\..\\escape")]
    public void Evidence_root_rejects_traversal_segments(string root)
    {
        Assert.Throws<ArgumentException>(() => EvidencePathPolicy.ResolveRoot(root));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("nested/escape.txt")]
    [InlineData("nested\\escape.txt")]
    public void Evidence_file_rejects_non_simple_names(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"evidence-policy-{Guid.NewGuid():N}");

        Assert.Throws<ArgumentException>(() => EvidencePathPolicy.ResolveFile(root, fileName));
    }

    [Fact]
    public void Evidence_file_is_confined_to_approved_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"evidence-policy-{Guid.NewGuid():N}");

        var path = EvidencePathPolicy.ResolveFile(root, "safe.snapshot.txt");

        Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(path));
    }
}
