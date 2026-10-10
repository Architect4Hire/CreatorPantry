using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for requesting and reading AIREC-002 structured first drafts.
/// </summary>
/// <remarks>
/// The workspace counterpart to <see cref="IAiProposalFacade"/>, for the same reason
/// <see cref="IAiConceptRequestFacade"/> is: a first-draft request names no recipe — there is none yet — so it
/// queues and polls at the workspace level rather than nested under one.
/// </remarks>
public interface IAiFirstDraftRequestFacade
{
    /// <summary>
    /// Queues a first-draft generation. Long work: this returns as soon as the request is recorded, and the
    /// model is called by the worker afterwards.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a retried request must not buy the same draft twice.
    /// </param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestRecipeFirstDraftViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Where a requested generation has got to, and the proposed draft once there is one.</summary>
    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Records what the creator decided about a draft, creating the recipe when they accepted one.
    /// </summary>
    /// <remarks>
    /// <strong>The only step that creates a recipe from a generated draft</strong>, and it either creates one
    /// and records the decision, or writes nothing at all. Retrying is safe and needs no idempotency key: a
    /// draft that has already been decided is recognised inside the transaction and answered with the recipe
    /// that decision created, never a second one.
    /// </remarks>
    Task<OperationResult<AiDraftAcceptanceServiceModel>> AcceptAsync(
        Guid requestId,
        AiDraftAcceptanceViewModel model,
        string actorUserId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiFirstDraftRequestFacade"/>
internal sealed class AiFirstDraftRequestFacade(
    IAiFirstDraftRequestBusiness business,
    IAiDraftAcceptanceBusiness acceptance,
    IValidator<RequestRecipeFirstDraftViewModel> validator,
    IValidator<AiDraftAcceptanceViewModel> acceptanceValidator,
    IWorkspaceFacade workspaces) : IAiFirstDraftRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestRecipeFirstDraftViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiFirstDraftRequestErrors.RequestInvalid,
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

        // Facade to facade: the setting is Tenancy's, and it is read for the workspace already resolved for
        // this scope -- never from the request, which has no field for it.
        var workspace = await workspaces.GetCurrentAsync(cancellationToken);

        return await business.RequestAsync(
            model, workspace.DefaultMeasurementSystem, idempotencyKey, cancellationToken);
    }

    public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(requestId, cancellationToken);

    public async Task<OperationResult<AiDraftAcceptanceServiceModel>> AcceptAsync(
        Guid requestId,
        AiDraftAcceptanceViewModel model,
        string actorUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await acceptanceValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<AiDraftAcceptanceServiceModel>.Failure(OperationError.Validation(
                AiDraftAcceptanceErrors.RequestInvalid,
                "The request is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return await acceptance.AcceptAsync(actorUserId, requestId, model, cancellationToken);
    }
}
