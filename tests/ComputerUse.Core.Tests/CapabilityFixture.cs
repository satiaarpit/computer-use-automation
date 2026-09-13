using ComputerUse.Core.Contracts;

namespace ComputerUse.Core.Tests;

internal static class CapabilityFixture
{
    public static CapabilityArtifact Create() => new()
    {
        CapabilityId = "search-and-extract.v1",
        Name = "Search and extract a result",
        Description = "Searches a permitted target and extracts the first matching result title.",
        Target = new CapabilityTarget
        {
            Surface = SurfaceKind.Web,
            EntryPointTemplate = new Uri("https://example.test/search")
        },
        Inputs =
        [
            new InputDefinition
            {
                Name = "query",
                Type = ValueTypeKind.String,
                Classification = DataClassification.Internal,
                Pattern = "^.{1,100}$"
            }
        ],
        Outputs =
        [
            new OutputDefinition
            {
                Name = "title",
                Type = ValueTypeKind.String,
                Classification = DataClassification.Public,
                Extraction = new ValueExtraction
                {
                    Target = Target(TargetStrategyKind.AccessibleRoleAndName, "heading:result"),
                    Kind = ExtractionKind.Text
                }
            }
        ],
        Steps =
        [
            new CapabilityStep
            {
                Id = "enter-query",
                Action = new SemanticAction
                {
                    Kind = ActionKind.Type,
                    Target = Target(TargetStrategyKind.Label, "Search"),
                    ValueTemplate = "${query}"
                }
            },
            new CapabilityStep
            {
                Id = "submit-search",
                Action = new SemanticAction
                {
                    Kind = ActionKind.Click,
                    Target = Target(TargetStrategyKind.AccessibleRoleAndName, "button:Search")
                }
            }
        ],
        Outcomes =
        [
            new KnownOutcome
            {
                Code = "no-results",
                Description = "The search completed without a matching result.",
                Kind = OutcomeKind.BusinessOutcome,
                Detection = new Condition
                {
                    Kind = ConditionKind.TextContains,
                    Target = Target(TargetStrategyKind.AccessibleRoleAndName, "status:results"),
                    ExpectedTemplate = "No results"
                }
            }
        ],
        Checkpoint = new Condition
        {
            Kind = ConditionKind.Visible,
            Target = Target(TargetStrategyKind.AccessibleRoleAndName, "heading:result")
        },
        Provenance = new CapabilityProvenance
        {
            DiscoveryRunId = "run-example",
            CreatedAt = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            GeneratorVersion = "0.1.0",
            Model = "configurable"
        }
    };

    private static TargetDescriptor Target(TargetStrategyKind kind, string value) => new()
    {
        Strategies = [new TargetStrategy { Kind = kind, Value = value }]
    };
}
