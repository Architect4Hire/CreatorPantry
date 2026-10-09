using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for reading a dish name into a cuisine, a dish type and a cooking method.
/// </summary>
/// <remarks>
/// The workspace counterpart to <see cref="IAiProposalFacade"/>, like <see cref="IAiConceptRequestFacade"/>:
/// this route names no recipe, so it queues and polls at the workspace level rather than nested under one.
/// </remarks>
public interface IAiDishFacetsRequestFacade
{
    /// <summary>
    /// Queues a dish-name reading. Long work: this returns as soon as the request is recorded, and the model
    /// is called by the worker afterwards.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Required. The reading spends a provider budget, so a retried request must not buy the same answer
    /// twice — and this capability is asked automatically when a creator types a name, which makes a
    /// duplicate request the normal case rather than the exceptional one.
    /// </param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestDishFacetsViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Where a requested reading has got to, and the suggested facets once there are any.</summary>
    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiDishFacetsRequestFacade"/>
internal sealed class AiDishFacetsRequestFacade(
    IAiDishFacetsRequestBusiness business,
    IValidator<RequestDishFacetsViewModel> validator) : IAiDishFacetsRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestDishFacetsViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiDishFacetsRequestErrors.RequestInvalid,
                    "The request is not valid.",
                    validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage)))),
                Replayed: false);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(new OperationError(
                    IdempotencyPolicy.KeyRequiredCode,
                    $"Supply an {IdempotencyPolicy.KeyHeader} header. Reading a name is not free, so a "
                        + "retried request must be able to return the first answer rather than buy a second.",
                    new Dictionary<string, string[]>())),
                Replayed: false);
        }

        return await business.RequestAsync(model, idempotencyKey, cancellationToken);
    }

    public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(requestId, cancellationToken);
}
