using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for requesting and reading AIREC-001 recipe concepts.
/// </summary>
/// <remarks>
/// The workspace counterpart to <see cref="IAiProposalFacade"/>: this route names no recipe, so it queues and
/// polls at the workspace level rather than nested under one.
/// </remarks>
public interface IAiConceptRequestFacade
{
    /// <summary>
    /// Queues a recipe-concept generation. Long work: this returns as soon as the request is recorded, and the
    /// model is called by the worker afterwards.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a retried request must not buy the same answer twice.
    /// </param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestRecipeConceptsViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Where a requested generation has got to, and the proposed concepts once there are any.</summary>
    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiConceptRequestFacade"/>
internal sealed class AiConceptRequestFacade(
    IAiConceptRequestBusiness business,
    IValidator<RequestRecipeConceptsViewModel> validator) : IAiConceptRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestRecipeConceptsViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiConceptRequestErrors.RequestInvalid,
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

        return await business.RequestAsync(model, idempotencyKey, cancellationToken);
    }

    public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(requestId, cancellationToken);
}
