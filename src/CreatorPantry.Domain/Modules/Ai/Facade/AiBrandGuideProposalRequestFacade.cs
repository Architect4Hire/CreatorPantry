using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>The application boundary for requesting a brand-guide proposal (11A.17).</summary>
public interface IAiBrandGuideProposalRequestFacade
{
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestBrandGuideProposalViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Records what a creator decided about proposed brand guidance, writing a new draft version of the guide
    /// from whatever they accepted (11A.18).
    /// </summary>
    /// <param name="actorUserId">The signed-in user, for the two audit entries this writes.</param>
    /// <param name="requestId">The request the proposal belongs to — the id the route names.</param>
    /// <param name="model">
    /// The decision, the items being accepted, and any wording the creator rewrote. It cannot name a guide, a
    /// version, a section key or a workspace: the guide comes from the proposal's own stored inputs, the
    /// section key from each item's dimension, and the workspace from the resolved context.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>No idempotency key, and it is not an omission.</strong> A proposal can be decided once: the
    /// operation moves to a terminal status, and a retry of the same decision is recognised by comparing what it
    /// asks for with what was recorded. That is a stronger guarantee than a key would give, because it survives
    /// the idempotency record expiring — and it is what stops a retry writing a second guide version.
    /// </para>
    /// <para>
    /// <strong>Editor, above the Contributor who may request a proposal.</strong> Asking produces something to
    /// read; accepting writes a version of the creator's own brand voice, which is what
    /// <c>IBrandStyleGuideFacade.CreateAsync</c> gates at Editor. Checked on this boundary as well as in the
    /// brand facade underneath, so the refusal names deciding rather than writing.
    /// </para>
    /// </remarks>
    Task<OperationResult<AiBrandGuideAcceptanceServiceModel>> AcceptAsync(
        string actorUserId,
        Guid requestId,
        AcceptBrandGuideProposalViewModel model,
        CancellationToken cancellationToken);
}

internal sealed class AiBrandGuideProposalRequestFacade(
    IAiBrandGuideProposalRequestBusiness business,
    IAiBrandGuideAcceptanceBusiness acceptance,
    IValidator<RequestBrandGuideProposalViewModel> validator,
    IValidator<AcceptBrandGuideProposalViewModel> acceptanceValidator) : IAiBrandGuideProposalRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestBrandGuideProposalViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiBrandGuideProposalRequestErrors.RequestInvalid,
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

    public async Task<OperationResult<AiBrandGuideAcceptanceServiceModel>> AcceptAsync(
        string actorUserId,
        Guid requestId,
        AcceptBrandGuideProposalViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await acceptanceValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            // Before anything is read, so a malformed decision costs no query and can touch nothing. Whether the
            // named items belong to this proposal is Business's question, because answering it needs the
            // proposal.
            return OperationResult<AiBrandGuideAcceptanceServiceModel>.Failure(OperationError.Validation(
                AiBrandGuideAcceptanceErrors.RequestInvalid,
                "That decision is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        // The role gate lives in Business, not here, unlike the proposal disposition seam's: this facade's other
        // operations are a request and a read, and putting one boundary's check in two places is how the two
        // drift. Business checks it before reading anything.
        return await acceptance.AcceptAsync(actorUserId, requestId, model, cancellationToken);
    }
}
