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
}

internal sealed class AiSeoPackageRequestFacade(
    IAiSeoPackageRequestBusiness business,
    IValidator<RequestSeoPackageViewModel> validator) : IAiSeoPackageRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId, RequestSeoPackageViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

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

        return await business.RequestAsync(recipeId, model, idempotencyKey, cancellationToken);
    }

    public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(recipeId, requestId, cancellationToken);
}
