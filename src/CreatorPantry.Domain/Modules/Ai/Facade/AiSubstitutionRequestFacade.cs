using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for requesting and reading AIREC-004 substitution advice.
/// </summary>
/// <remarks>
/// Recipe-bound, like <see cref="IAiRevisionRequestFacade"/>, because the advice depends on what the
/// ingredient is doing in a particular method. Unlike that one, nothing it produces can be applied: there is
/// no acceptance operation here and no disposition to make, because a substitution proposes no change to
/// accept. A creator reads the alternatives and edits the recipe themselves.
/// </remarks>
public interface IAiSubstitutionRequestFacade
{
    /// <summary>
    /// Queues substitution advice for one ingredient. Long work: this returns as soon as the request is
    /// recorded, and the model is called by the worker afterwards.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a retried request must not buy the same answer twice.
    /// </param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestIngredientSubstitutionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Where a requested substitution has got to, and the alternatives once there are any.</summary>
    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId, Guid requestId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiSubstitutionRequestFacade"/>
internal sealed class AiSubstitutionRequestFacade(
    IAiSubstitutionRequestBusiness business,
    IValidator<RequestIngredientSubstitutionViewModel> validator) : IAiSubstitutionRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestIngredientSubstitutionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiSubstitutionRequestErrors.RequestInvalid,
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
