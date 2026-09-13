using ComputerUse.Core.Contracts;

namespace ComputerUse.Core.Tests;

public sealed class ExecutionResultTests
{
    [Theory]
    [InlineData(ExecutionStatus.Success)]
    [InlineData(ExecutionStatus.BusinessOutcome)]
    [InlineData(ExecutionStatus.InterventionRequired)]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.Failure)]
    public void ResultTaxonomy_RepresentsEveryTerminalStatus(ExecutionStatus status)
    {
        var result = new ExecutionResult
        {
            RunId = "run-1",
            CapabilityId = "capability-1",
            Status = status,
            OutcomeCode = status == ExecutionStatus.BusinessOutcome ? "no-results" : null,
            Failure = status == ExecutionStatus.Failure
                ? new FailureDetail { Category = "target", Message = "Target was not found." }
                : null
        };

        Assert.Equal(status, result.Status);
        Assert.Equal(status == ExecutionStatus.Failure, result.Failure is not null);
    }
}
