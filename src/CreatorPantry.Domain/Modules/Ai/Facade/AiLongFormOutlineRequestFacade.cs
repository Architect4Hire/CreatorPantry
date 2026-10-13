using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// LF.2.2's outline requests: starting a long-form draft, asking for its outline again, and following a
/// request. The draft itself — reading it, editing its plan, approving it — is the content module's.
/// </summary>
public interface IAiLongFormOutlineRequestFacade
{
    /// <inheritdoc cref="IAiLongFormOutlineRequestBusiness.StartAsync"/>
    Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> StartAsync(
        StartLongFormDraftViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <inheritdoc cref="IAiLongFormOutlineRequestBusiness.RequestAgainAsync"/>
    Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> RequestAgainAsync(
        Guid draftId, RequestLongFormOutlineViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <inheritdoc cref="IAiLongFormOutlineRequestBusiness.GetAsync"/>
    Task<OperationResult<LongFormOutlineRequestStatusServiceModel>> GetAsync(
        Guid draftId, Guid requestId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiLongFormOutlineRequestFacade"/>
internal sealed class AiLongFormOutlineRequestFacade(
    IAiLongFormOutlineRequestBusiness business,
    IValidator<StartLongFormDraftViewModel> startValidator,
    IValidator<RequestLongFormOutlineViewModel> againValidator) : IAiLongFormOutlineRequestFacade
{
    public async Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> StartAsync(
        StartLongFormDraftViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Refusal(await startValidator.ValidateAsync(model, cancellationToken), idempotencyKey)
            ?? await business.StartAsync(model, idempotencyKey!, cancellationToken);
    }

    public async Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> RequestAgainAsync(
        Guid draftId, RequestLongFormOutlineViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Refusal(await againValidator.ValidateAsync(model, cancellationToken), idempotencyKey)
            ?? await business.RequestAgainAsync(draftId, model, idempotencyKey!, cancellationToken);
    }

    public Task<OperationResult<LongFormOutlineRequestStatusServiceModel>> GetAsync(
        Guid draftId, Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(draftId, requestId, cancellationToken);

    private static IdempotentOutcome<StartedLongFormDraftServiceModel>? Refusal(
        FluentValidation.Results.ValidationResult validation, string? idempotencyKey)
    {
        if (!validation.IsValid)
        {
            return Refuse(OperationError.Validation(
                AiLongFormOutlineRequestErrors.RequestInvalid,
                "The request is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return string.IsNullOrWhiteSpace(idempotencyKey)
            ? Refuse(new OperationError(
                IdempotencyPolicy.KeyRequiredCode,
                $"Supply an {IdempotencyPolicy.KeyHeader} header. Planning a piece is not free, so a retried "
                    + "request must be able to return the first answer rather than start a second draft.",
                new Dictionary<string, string[]>()))
            : null;
    }

    private static IdempotentOutcome<StartedLongFormDraftServiceModel> Refuse(OperationError error) =>
        new(OperationResult<StartedLongFormDraftServiceModel>.Failure(error), Replayed: false);
}
