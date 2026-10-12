using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The request seam for writing a long-form draft's sections (LF.3.3): one section, every section not yet
/// written, and where each section stands.
/// </summary>
/// <remarks>
/// A creator's own edit to a section, and its settled flag, are not here: they call no model and spend no
/// allowance, so they are the content module's (<c>ILongFormSectionFacade</c>).
/// </remarks>
public interface IAiLongFormSectionRequestFacade
{
    /// <summary>Queues the writing of one section. Requires an idempotency key.</summary>
    Task<IdempotentOutcome<LongFormSectionRequestedServiceModel>> RequestAsync(
        Guid draftId, Guid sectionId, RequestLongFormSectionViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Queues the writing of every section that is not written, settled or already being written — all of them
    /// or none. Requires an idempotency key.
    /// </summary>
    Task<IdempotentOutcome<LongFormSectionBatchServiceModel>> RequestRestAsync(
        Guid draftId, RequestLongFormSectionBatchViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The draft's sections with their text, and the latest writing request for each.</summary>
    Task<OperationResult<LongFormSectionsStatusServiceModel>> GetSectionsAsync(Guid draftId, CancellationToken cancellationToken);

    /// <summary>
    /// The draft read whole for review: the assembled document, what was found about each generated section,
    /// and the latest writing request for each.
    /// </summary>
    Task<OperationResult<LongFormDocumentReviewServiceModel>> GetDocumentAsync(Guid draftId, CancellationToken cancellationToken);
}

internal sealed class AiLongFormSectionRequestFacade(
    IAiLongFormSectionRequestBusiness business,
    IValidator<RequestLongFormSectionViewModel> oneValidator,
    IValidator<RequestLongFormSectionBatchViewModel> restValidator) : IAiLongFormSectionRequestFacade
{
    public async Task<IdempotentOutcome<LongFormSectionRequestedServiceModel>> RequestAsync(
        Guid draftId, Guid sectionId, RequestLongFormSectionViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var refusal = Refusal(
            await oneValidator.ValidateAsync(model, cancellationToken), idempotencyKey, IdempotencyPolicy.KeyMaxLength);

        return refusal is null
            ? await business.RequestAsync(draftId, sectionId, model, idempotencyKey!, cancellationToken)
            : Refuse<LongFormSectionRequestedServiceModel>(refusal);
    }

    public async Task<IdempotentOutcome<LongFormSectionBatchServiceModel>> RequestRestAsync(
        Guid draftId, RequestLongFormSectionBatchViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var refusal = Refusal(
            await restValidator.ValidateAsync(model, cancellationToken),
            idempotencyKey,
            AiLongFormSectionRequestPolicy.BatchKeyMaxLength);

        return refusal is null
            ? await business.RequestRestAsync(draftId, model, idempotencyKey!, cancellationToken)
            : Refuse<LongFormSectionBatchServiceModel>(refusal);
    }

    public Task<OperationResult<LongFormSectionsStatusServiceModel>> GetSectionsAsync(
        Guid draftId, CancellationToken cancellationToken) =>
        business.GetSectionsAsync(draftId, cancellationToken);

    public Task<OperationResult<LongFormDocumentReviewServiceModel>> GetDocumentAsync(
        Guid draftId, CancellationToken cancellationToken) =>
        business.GetDocumentAsync(draftId, cancellationToken);

    private static OperationError? Refusal(
        FluentValidation.Results.ValidationResult validation, string? idempotencyKey, int keyMaxLength)
    {
        if (!validation.IsValid)
        {
            return OperationError.Validation(
                AiLongFormSectionRequestErrors.RequestInvalid,
                "The request is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage)));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new OperationError(
                IdempotencyPolicy.KeyRequiredCode,
                $"Supply an {IdempotencyPolicy.KeyHeader} header. Writing a section is not free, so a retried "
                    + "request must be able to return the first answer rather than write the section twice.",
                new Dictionary<string, string[]>());
        }

        // Printable ASCII and within the column, as every idempotency key is. Checked here because this key is
        // stored on the operation itself, and for "write the rest" with a section's id after it.
        return idempotencyKey.Length > keyMaxLength || idempotencyKey.Any(character => character is < '!' or > '~')
            ? OperationError.Validation(
                AiLongFormSectionRequestErrors.RequestInvalid,
                "The request is not valid.",
                [(IdempotencyPolicy.KeyHeader, $"The key is 1 to {keyMaxLength} printable ASCII characters.")])
            : null;
    }

    private static IdempotentOutcome<T> Refuse<T>(OperationError error) =>
        new(OperationResult<T>.Failure(error), Replayed: false);
}
