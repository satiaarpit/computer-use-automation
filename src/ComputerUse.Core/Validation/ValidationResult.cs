namespace ComputerUse.Core.Validation;

public sealed record ValidationIssue(string Path, string Code, string Message);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static ValidationResult Success { get; } = new([]);
}
