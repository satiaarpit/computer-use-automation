using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Evidence;
using ComputerUse.Core.Serialization;

namespace ComputerUse.Replay;

public static class ReplayResultEvidenceWriter
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new(CapabilityJson.Options)
    {
        WriteIndented = true
    };

    public static async Task<string?> WriteBestEffortAsync(
        ReplayRunResult result,
        CapabilityArtifact artifact,
        IReadOnlyDictionary<string, JsonElement> suppliedInputs,
        string evidenceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(suppliedInputs);

        try
        {
            var approvedRoot = EvidencePathPolicy.ResolveRoot(evidenceRoot);
            Directory.CreateDirectory(approvedRoot);
            var path = EvidencePathPolicy.ResolveFile(approvedRoot, $"{result.RunId}.result.json");
            var node = JsonSerializer.SerializeToNode(result, ResultJsonOptions)
                ?? throw new JsonException("The replay result could not be serialized.");
            if (node is not JsonObject root)
            {
                throw new JsonException("The replay result root must be a JSON object.");
            }
            root["outputCommitments"] = CreateOutputCommitments(result.Outputs);
            RedactClassifiedOutputs(root, artifact.Outputs);
            var protectedInputValues = artifact.Inputs
                .Where(input => input.Classification != DataClassification.Public)
                .Where(input => suppliedInputs.ContainsKey(input.Name))
                .Select(input => suppliedInputs[input.Name].ToString());
            SanitizeResult(root, new EvidenceRedactor(protectedInputValues));
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, node, ResultJsonOptions, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    private static void RedactClassifiedOutputs(JsonObject root, IReadOnlyList<OutputDefinition> definitions)
    {
        if (root["outputs"] is not JsonObject outputs)
        {
            return;
        }

        foreach (var definition in definitions.Where(output => output.IsSensitive))
        {
            if (outputs.ContainsKey(definition.Name))
            {
                outputs[definition.Name] = "[REDACTED]";
            }
        }
    }

    private static JsonObject CreateOutputCommitments(IReadOnlyDictionary<string, JsonElement> outputs)
    {
        var commitments = new JsonObject();
        foreach (var output in outputs.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(output.Value.GetRawText()));
            commitments[output.Key] = Convert.ToHexString(digest).ToLowerInvariant();
        }

        return commitments;
    }

    private static void SanitizeResult(JsonObject root, EvidenceRedactor redactor)
    {
        foreach (var property in root.ToArray())
        {
            if (string.Equals(property.Key, "runId", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(property.Key, "outputCommitments", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(property.Key, "evidence", StringComparison.Ordinal) && property.Value is JsonArray evidence)
            {
                foreach (var item in evidence.OfType<JsonObject>())
                {
                    SanitizeEvidenceReference(item, redactor);
                }
                continue;
            }

            if (string.Equals(property.Key, "interventionSummary", StringComparison.Ordinal) &&
                property.Value is JsonObject interventionSummary)
            {
                SanitizeInterventionSummary(interventionSummary, redactor);
                continue;
            }

            if (property.Value is not null)
            {
                Sanitize(property.Value, redactor);
            }
        }
    }

    private static void SanitizeInterventionSummary(JsonObject summary, EvidenceRedactor redactor)
    {
        foreach (var property in summary.ToArray())
        {
            if (string.Equals(property.Key, "sessionContinuityCommitment", StringComparison.Ordinal))
            {
                continue;
            }

            if (property.Value is not null)
            {
                Sanitize(property.Value, redactor);
            }
        }
    }

    private static void SanitizeEvidenceReference(JsonObject evidence, EvidenceRedactor redactor)
    {
        foreach (var property in evidence.ToArray())
        {
            if (string.Equals(property.Key, "relativePath", StringComparison.Ordinal))
            {
                continue;
            }

            if (property.Value is not null)
            {
                Sanitize(property.Value, redactor);
            }
        }
    }

    private static void Sanitize(JsonNode node, EvidenceRedactor redactor)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    jsonObject[property.Key] = redactor.Redact(text);
                }
                else if (property.Value is not null)
                {
                    Sanitize(property.Value, redactor);
                }
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                if (jsonArray[index] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    jsonArray[index] = redactor.Redact(text);
                }
                else if (jsonArray[index] is not null)
                {
                    Sanitize(jsonArray[index]!, redactor);
                }
            }
        }
    }
}