namespace CreatorPantry.Domain.Managers.Prompts;

/// <summary>
/// A template that cannot be trusted to produce a prompt: malformed, mis-declared, checksum-mismatched,
/// duplicated, absent, or rendered without a required value.
/// </summary>
/// <remarks>
/// An exception rather than an <see cref="Results.OperationError"/> because none of these are expected
/// application failures a creator could cause or resolve. A template is a code-adjacent contract: it is read
/// at startup so a bad one stops the host rather than a request, and its inputs are assembled from domain
/// data the server already holds, so a missing one is a caller bug.
/// </remarks>
public sealed class PromptTemplateException : Exception
{
    public PromptTemplateException(string message)
        : base(message)
    {
    }

    /// <param name="origin">The resource name or template identity the failure is about.</param>
    public PromptTemplateException(string origin, string message)
        : base($"Prompt template '{origin}': {message}") => Origin = origin;

    /// <summary>The resource name or template identity the failure is about, when one is known.</summary>
    public string? Origin { get; }
}
