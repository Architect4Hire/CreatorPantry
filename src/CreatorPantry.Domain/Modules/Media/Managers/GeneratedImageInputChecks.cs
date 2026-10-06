namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Shape checks on an image-generation request, independent of where the request came from.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in a FluentValidation validator because there is no ViewModel to validate yet: the
/// callers of this seam are the worker's tests and, when 12.10c lands, a controller that will bring its own
/// validator and map into <see cref="GeneratedImageRequest"/>. Putting the rules in one place now means the
/// controller inherits them rather than restating them.
/// </para>
/// <para>
/// The variant bound is the same number the database's check constraint enforces. Both, deliberately: the
/// constraint is the guarantee and this is the message a creator can act on.
/// </para>
/// </remarks>
public static class GeneratedImageInputChecks
{
    /// <summary>The most characters a prompt may carry, matched to the column.</summary>
    public const int PromptTextMaxLength = 4000;

    /// <summary>The most characters an idempotency key may carry, matched to the column.</summary>
    public const int IdempotencyKeyMaxLength = 200;

    public static IEnumerable<(string Field, string Error)> Request(GeneratedImageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.PromptText))
        {
            yield return (nameof(request.PromptText), "A prompt is required.");
        }
        else if (request.PromptText.Length > PromptTextMaxLength)
        {
            yield return (nameof(request.PromptText),
                $"A prompt may be at most {PromptTextMaxLength} characters.");
        }

        if (request.AvoidText is { Length: > PromptTextMaxLength })
        {
            yield return (nameof(request.AvoidText),
                $"An avoid list may be at most {PromptTextMaxLength} characters.");
        }

        if (request.VariantCount < MediaPolicy.MinVariantsPerOperation
            || request.VariantCount > MediaPolicy.MaxVariantsPerOperation)
        {
            yield return (nameof(request.VariantCount),
                $"Ask for between {MediaPolicy.MinVariantsPerOperation} and "
                    + $"{MediaPolicy.MaxVariantsPerOperation} images.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            // Required rather than generated when absent. A key the server invented would make every
            // repeated request a new generation, which is the charge this field exists to prevent.
            yield return (nameof(request.IdempotencyKey), "An idempotency key is required.");
        }
        else if (request.IdempotencyKey.Length > IdempotencyKeyMaxLength)
        {
            yield return (nameof(request.IdempotencyKey),
                $"An idempotency key may be at most {IdempotencyKeyMaxLength} characters.");
        }
    }
}
