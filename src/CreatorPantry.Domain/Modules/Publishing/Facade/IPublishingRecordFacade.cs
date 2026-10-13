using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Publishing.Facade;

/// <summary>
/// What controllers and workers call to confirm a publication and to read one back (PUB-002).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Confirming takes the Editor role.</strong> Publishing puts a workspace's content in public under
/// its own name, which auth.md counts among the sensitive operations; a Contributor can prepare the card and
/// the copy, and an Editor confirms the delivery. Reading a record takes the lowest role, as every other read
/// of workspace-owned content does.
/// </para>
/// <para>
/// <strong>The idempotency key is required here</strong> rather than defaulted, because the effect of this
/// command is outside this server and the provider has no key of its own. A caller without one is told so
/// before anything is validated or written (api-contract.md).
/// </para>
/// <para>
/// <strong>It does not wrap the command in <c>IIdempotentCommandExecutor</c></strong>, whose contract is one
/// short transaction that commits with the stored result — which this operation, spanning a provider call and
/// two commits, cannot honour. The ledger's own unique key is the guard instead, as the image-generation
/// command's is, and it keeps working after the shared record's retention window has passed.
/// </para>
/// </remarks>
public interface IPublishingRecordFacade
{
    /// <inheritdoc cref="IPublishingRecordBusiness.ScheduleAsync"/>
    Task<OperationResult<PublishingScheduleOutcome>> ScheduleAsync(
        string userId, SchedulePublishingViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordBusiness.GetAsync"/>
    Task<OperationResult<PublishingRecordServiceModel>> GetAsync(Guid recordId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordBusiness.ListForCardAsync"/>
    /// <remarks>Every member including a Viewer may read, as every other read of workspace-owned content is.</remarks>
    Task<PublishingRecordListServiceModel> ListForCardAsync(Guid contentCardId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IPublishingRecordBusiness.CancelAsync"/>
    /// <remarks>
    /// Editor, as confirming is: stopping a publication is the same decision in reverse. No idempotency key —
    /// the record's own id is what makes a repeated cancellation converge rather than accumulate, so there is
    /// nothing for a key to protect.
    /// </remarks>
    Task<OperationResult<PublishingRecordServiceModel>> CancelAsync(
        string userId, Guid recordId, CancelPublishingViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPublishingRecordFacade"/>
internal sealed class PublishingRecordFacade(
    IValidator<SchedulePublishingViewModel> validator,
    IValidator<CancelPublishingViewModel> cancelValidator,
    IPublishingRecordBusiness business,
    IWorkspaceContext workspace) : IPublishingRecordFacade
{
    public async Task<OperationResult<PublishingScheduleOutcome>> ScheduleAsync(
        string userId, SchedulePublishingViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Editor)
        {
            return OperationResult<PublishingScheduleOutcome>.Failure(new OperationError(
                PublishingErrorCodes.ScheduleForbidden,
                "Confirming a publication takes the Editor role in this workspace.",
                new Dictionary<string, string[]>()));
        }

        // The key first, and with the shared policy's own codes: a command whose effect is outside this
        // server is not validated at all until a retry of it is known to be safe.
        if (SchedulePublishingInputChecks.Key(idempotencyKey) is { } keyFailure)
        {
            return OperationResult<PublishingScheduleOutcome>.Failure(keyFailure);
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<PublishingScheduleOutcome>.Failure(OperationError.Validation(
                PublishingErrorCodes.ScheduleInvalid,
                "That publication could not be scheduled as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.ScheduleAsync(userId, model, idempotencyKey!, cancellationToken);
    }

    public Task<OperationResult<PublishingRecordServiceModel>> GetAsync(
        Guid recordId, CancellationToken cancellationToken) =>
        business.GetAsync(recordId, cancellationToken);

    public Task<PublishingRecordListServiceModel> ListForCardAsync(
        Guid contentCardId, CancellationToken cancellationToken) =>
        business.ListForCardAsync(contentCardId, cancellationToken);

    public async Task<OperationResult<PublishingRecordServiceModel>> CancelAsync(
        string userId, Guid recordId, CancelPublishingViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (workspace.Role < WorkspaceRole.Editor)
        {
            return OperationResult<PublishingRecordServiceModel>.Failure(new OperationError(
                PublishingErrorCodes.CancelForbidden,
                "Cancelling a publication takes the Editor role in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await cancelValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<PublishingRecordServiceModel>.Failure(OperationError.Validation(
                PublishingErrorCodes.CancelInvalid,
                "That publication could not be cancelled as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.CancelAsync(userId, recordId, model, cancellationToken);
    }
}
