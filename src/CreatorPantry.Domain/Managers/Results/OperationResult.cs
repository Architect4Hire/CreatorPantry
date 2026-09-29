namespace CreatorPantry.Domain.Managers.Results;

/// <summary>An expected application failure with a stable, machine-readable code.</summary>
/// <param name="Code">Stable error code, e.g. <c>auth.registration.invalid</c>.</param>
/// <param name="Message">Human-readable summary.</param>
/// <param name="FieldErrors">Errors keyed by camelCase request field name.</param>
/// <param name="Extensions">
/// Structured facts about this refusal, surfaced as ProblemDetails extensions beside <c>code</c>.
/// </param>
/// <remarks>
/// <para>
/// <strong><paramref name="Extensions"/> is for what a caller has to <em>act</em> on, not for explaining the
/// refusal.</strong> The message explains it. USAGE-007 is the case that needed this: a refused AI request has
/// to say what is exhausted, what remains and when it resets, and a client that had to parse those numbers back
/// out of an English sentence would break the first time the sentence was reworded.
/// </para>
/// <para>
/// <strong>It is never a place to put something the caller may not see.</strong> Everything here is serialized
/// verbatim into the response body, so it carries the caller's own facts only — never another account's
/// figures, never another workspace's existence, and never anything a 404 was chosen to withhold.
/// </para>
/// <para>
/// Values must be JSON-serializable. <see cref="FieldErrors"/> stays separate because it has its own place in
/// the <c>ValidationProblemDetails</c> shape and its own meaning: which field the caller got wrong.
/// </para>
/// </remarks>
public sealed record OperationError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]> FieldErrors,
    IReadOnlyDictionary<string, object?>? Extensions = null)
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
