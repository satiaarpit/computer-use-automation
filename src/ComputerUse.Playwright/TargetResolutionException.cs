namespace ComputerUse.Playwright;

public sealed class TargetResolutionException : InvalidOperationException
{
    public TargetResolutionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
