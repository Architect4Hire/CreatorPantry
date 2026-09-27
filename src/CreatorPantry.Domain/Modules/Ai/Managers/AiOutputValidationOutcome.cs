namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// A validated model answer of any capability's own document type, or the reason there isn't one.
/// </summary>
/// <remarks>
/// The generic counterpart to <see cref="AiOutputValidationResult"/>, which stays fixed to
/// <see cref="AiOutputDocument"/> for the recipe-diff capabilities that already call it directly. This type is
/// what <see cref="CreatorPantry.Domain.Modules.Ai.Gateways.IAiCompletionGateway"/> hands back, so a capability
/// whose answer is not a diff — <see cref="AiConceptOutputDocument"/>, for one — is held to its own schema and
/// its own domain rules without forcing its shape through the recipe-diff document.
/// </remarks>
public sealed class AiOutputValidationOutcome<TDocument>
    where TDocument : class
{
    private AiOutputValidationOutcome(TDocument? document, AiOutputFailure? failure) =>
        (Document, Failure) = (document, failure);

    public TDocument? Document { get; }

    public AiOutputFailure? Failure { get; }

    public bool Succeeded => Failure is null;

    public static AiOutputValidationOutcome<TDocument> Success(TDocument document) => new(document, null);

    public static AiOutputValidationOutcome<TDocument> Failed(AiOutputFailure failure) => new(null, failure);
}

/// <summary>
/// Validates a provider's raw payload into one capability's own document type.
/// </summary>
/// <remarks>
/// The seam <see cref="CreatorPantry.Domain.Modules.Ai.Gateways.IAiCompletionGateway"/> calls instead of
/// hardcoding <see cref="AiOutputValidator.Validate"/>. A capability whose answer is diff-shaped supplies
/// <see cref="AiOutputValidator.AsDelegate"/>; one that is not supplies its own validator the same way — the
/// gateway's own job (timing, retrying, recording attempts) does not change either way.
/// </remarks>
public delegate AiOutputValidationOutcome<TDocument> AiOutputValidatorDelegate<TDocument>(
    string? payload, string expectedSchemaVersion, AiOperationScope scope)
    where TDocument : class;
