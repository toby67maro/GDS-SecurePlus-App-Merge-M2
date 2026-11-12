namespace EngineeringWorkspace.Application.Common;

/// <summary>
/// An actionable, UI-safe description of a failure: what failed, the likely category, safe technical detail
/// and the recommended action. Never contains secrets or stack traces.
/// </summary>
public sealed record OperationError(string Category, string Message, string? Detail = null, string? RecommendedAction = null)
{
    public override string ToString() =>
        string.Join(" ", new[] { Message, Detail, RecommendedAction }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

/// <summary>Outcome of an operation that returns no value.</summary>
public sealed record OperationResult(bool Succeeded, OperationError? Error)
{
    public static OperationResult Success() => new(true, null);
    public static OperationResult Failure(OperationError error) => new(false, error);
    public static OperationResult Failure(string category, string message, string? detail = null, string? action = null) =>
        new(false, new OperationError(category, message, detail, action));
}

/// <summary>Outcome of an operation that returns a value on success.</summary>
public sealed record OperationResult<T>(bool Succeeded, T? Value, OperationError? Error)
{
    public static OperationResult<T> Success(T value) => new(true, value, null);
    public static OperationResult<T> Failure(OperationError error) => new(false, default, error);
    public static OperationResult<T> Failure(string category, string message, string? detail = null, string? action = null) =>
        new(false, default, new OperationError(category, message, detail, action));
}
