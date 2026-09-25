using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for requesting and reading AI proposals, reused by controllers and by the worker.
/// </summary>
public interface IAiProposalFacade
{
    /// <summary>
    /// Queues a proposal for a recipe. Long work: this returns as soon as the request is recorded, and the
    /// model is called by the worker afterwards.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Required. Generation spends a provider budget, so a retried request must not buy the same answer twice
    /// — a caller with no natural key is given a generated one at the edge rather than allowed to omit it.
    /// </param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestAiProposalViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Where a requested proposal has got to, and the proposal once there is one.</summary>
    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId,
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what a creator decided about a proposal they have reviewed, applying whatever they accepted.
    /// </summary>
    /// <param name="actorUserId">The signed-in user, for the audit entry the decision writes.</param>
    /// <param name="requestId">The request the proposal belongs to — the id the route names.</param>
    /// <param name="model">
    /// The decision and the confirmation naming what is accepted. It cannot name a value, a field, a target, a
    /// recipe, a version or a workspace — see <see cref="AiProposalDispositionViewModel"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>No idempotency key, and it is not an omission.</strong> A proposal can be decided once: the
    /// operation moves to a terminal status, and a retry of the same decision is recognised by comparing what it
    /// asks for with what was recorded. That is a stronger guarantee than a key would give, because it survives
    /// the idempotency record expiring — the same reasoning
    /// <see cref="RequestAsync"/> records for why an operation carries its own key.
    /// </para>
    /// <para>
    /// <strong>Contributor, checked here as well as in the recipe facade underneath.</strong> Not redundancy: the
    /// recipe facade's check only runs when something is accepted, and a rejection reaches no recipe at all — so
    /// without a check on this boundary a caller who is not an HTTP request could close a proposal against a
    /// workspace's content without a role. Accepting is an edit and costs what typing the edit out by hand costs;
    /// deciding a proposal's fate costs the same, because it decides the fate of generated content aimed at that
    /// creator's recipe.
    /// </para>
    /// </remarks>
    Task<OperationResult<AiProposalDispositionServiceModel>> DispositionAsync(
        string actorUserId,
        Guid recipeId,
        Guid requestId,
        AiProposalDispositionViewModel model,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiProposalFacade"/>
internal sealed class AiProposalFacade(
    IAiProposalBusiness business,
    IWorkspaceContext workspace,
    IValidator<RequestAiProposalViewModel> validator,
    IValidator<AiProposalDispositionViewModel> dispositionValidator) : IAiProposalFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestAiProposalViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiProposalErrors.TaskUnknown,
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

        // Replay is decided by the operation's own idempotency key rather than by the generic idempotency
        // record: that record expires, and an operation is permanent, so a request arriving after the record
        // has aged out still finds the operation it already created.
        return await business.RequestAsync(recipeId, model, idempotencyKey, cancellationToken);
    }

    public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId,
        Guid requestId,
        CancellationToken cancellationToken) =>
        business.GetAsync(recipeId, requestId, cancellationToken);

    public async Task<OperationResult<AiProposalDispositionServiceModel>> DispositionAsync(
        string actorUserId,
        Guid recipeId,
        Guid requestId,
        AiProposalDispositionViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<AiProposalDispositionServiceModel>.Failure(new OperationError(
                AiProposalErrors.DispositionForbidden,
                "You do not have permission to decide about AI proposals in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await dispositionValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            // Before anything is read, so a malformed confirmation costs no query and can touch nothing. Whether
            // the named changes belong to this proposal is Business's question, because answering it needs the
            // proposal.
            return OperationResult<AiProposalDispositionServiceModel>.Failure(OperationError.Validation(
                AiProposalErrors.SelectionInvalid,
                "That disposition is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        return await business.DispositionAsync(actorUserId, recipeId, requestId, model, cancellationToken);
    }
}
