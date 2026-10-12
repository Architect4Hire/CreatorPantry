using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>The application boundary for requesting an SEO package (RCPUB-002).</summary>
public interface IAiSeoPackageRequestFacade
{
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId, RequestSeoPackageViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);

    /// <summary>The same package with an accepted long-form draft as its subject (LF.5.3).</summary>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestForLongFormAsync(
        Guid draftId, RequestLongFormSeoPackageViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetForLongFormAsync(
        Guid draftId, Guid requestId, CancellationToken cancellationToken);
}

internal sealed class AiSeoPackageRequestFacade(
    IAiSeoPackageRequestBusiness business,
    IValidator<RequestSeoPackageViewModel> validator,
    IValidator<RequestLongFormSeoPackageViewModel> longFormValidator) : IAiSeoPackageRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId, RequestSeoPackageViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Refusal(await validator.ValidateAsync(model, cancellationToken), idempotencyKey)
            ?? await business.RequestAsync(recipeId, model, idempotencyKey!, cancellationToken);
    }

    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestForLongFormAsync(
        Guid draftId, RequestLongFormSeoPackageViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        return Refusal(await longFormValidator.ValidateAsync(model, cancellationToken), idempotencyKey)
            ?? await business.RequestForLongFormAsync(draftId, model, idempotencyKey!, cancellationToken);
    }

    public Task<OperationResult<AiProposalStatusServiceModel>> GetForLongFormAsync(
        Guid draftId, Guid requestId, CancellationToken cancellationToken) =>
        business.GetForLongFormAsync(draftId, requestId, cancellationToken);

    /// <summary>Why a request goes no further, whichever subject it names; null when it may.</summary>
    private static IdempotentOutcome<AiProposalStatusServiceModel>? Refusal(
        FluentValidation.Results.ValidationResult validation, string? idempotencyKey)
    {
        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiSeoPackageRequestErrors.RequestInvalid,
                    "The request is not valid.",
                    validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage)))),
                Replayed: false);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(new OperationError(
                    IdempotencyPolicy.KeyRequiredCode,
                    $"Supply an {IdempotencyPolicy.KeyHeader} header. Generation is not free, so a retried "
                        + "request must be able to return the first answer rather than buy a second.",
                    new Dictionary<string, string[]>())),
                Replayed: false);
        }

        return null;
    }

    public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(recipeId, requestId, cancellationToken);
}
