using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for requesting and reading AIREC-003 scoped revisions.
/// </summary>
/// <remarks>
/// Recipe-bound, so it nests under one — unlike <see cref="IAiConceptRequestFacade"/> and
/// <see cref="IAiFirstDraftRequestFacade"/>, which name no recipe because there is none yet. Reviewing and
/// deciding a revision is the existing proposal seam's job: a revision produces an ordinary server-computed
/// diff against a pinned version, which is exactly what that seam already reviews.
/// </remarks>
public interface IAiRevisionRequestFacade
{
    /// <summary>
    /// Queues a scoped revision. Long work: this returns as soon as the request is recorded, and the model is
    /// called by the worker afterwards.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a retried request must not buy the same answer twice.
    /// </param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestRecipeRevisionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Where a requested revision has got to, and the proposed changes once there are any.</summary>
    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiRevisionRequestFacade"/>
internal sealed class AiRevisionRequestFacade(
    IAiRevisionRequestBusiness business,
    IValidator<RequestRecipeRevisionViewModel> validator) : IAiRevisionRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestRecipeRevisionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiRevisionRequestErrors.RequestInvalid,
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
