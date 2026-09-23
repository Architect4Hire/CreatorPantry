namespace CreatorPantry.Domain.Managers.Results;

/// <summary>An expected application failure with a stable, machine-readable code.</summary>
/// <param name="Code">Stable error code, e.g. <c>auth.registration.invalid</c>.</param>
/// <param name="Message">Human-readable summary.</param>
/// <param name="FieldErrors">Errors keyed by camelCase request field name.</param>
public sealed record OperationError(string Code, string Message, IReadOnlyDictionary<string, string[]> FieldErrors)
{
    public static OperationError Validation(string code, string message, IEnumerable<(string Field, string Error)> errors) =>
        new(code, message, errors
            .GroupBy(error => ToCamelCase(error.Field))
            .ToDictionary(group => group.Key, group => group.Select(error => error.Error).ToArray()));

    private static string ToCamelCase(string field) =>
        string.IsNullOrEmpty(field) ? field : char.ToLowerInvariant(field[0]) + field[1..];
}

/// <summary>The outcome of a facade operation: a value, or an expected <see cref="OperationError"/>.</summary>
public sealed class OperationResult<T>
{
    private OperationResult(T? value, OperationError? error) => (Value, Error) = (value, error);

    public T? Value { get; }

    public OperationError? Error { get; }

    public bool Succeeded => Error is null;

    public static OperationResult<T> Success(T value) => new(value, null);

    public static OperationResult<T> Failure(OperationError error) => new(default, error);
}
