using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The application boundary for the creator's prompt library. One operation so far: saving a prompt.
/// </summary>
/// <remarks>
/// <strong>This is the seam DAM-001 calls</strong> when a generated asset is committed, from inside its own
/// transaction, so the asset row and the prompt row commit together — see
/// <c>PromptRecordDataLayer.SaveAsync</c> for why the direction is that way round and not the reverse. It is
/// also what the Image Studio's save (12.10c) reaches through the HTTP route.
/// </remarks>
public interface IPromptRecordFacade
{
    /// <summary>
    /// Saves a prompt to the resolved workspace's library. Contributor or above.
    /// </summary>
    /// <param name="userId">The authenticated caller, for the idempotency scope. Never from request input.</param>
    /// <param name="model">The prompt and its lineage. Carries no workspace, author, id or asset reference.</param>
    /// <param name="idempotencyKey">
    /// Optional, and worth sending: without it, a client whose save committed but whose response was lost
    /// retries and saves a second, permanent copy of the same prompt. With it, the retry returns the first
    /// answer.
    /// </param>
    Task<IdempotentOutcome<SavedPromptRecordServiceModel>> SaveAsync(
        string userId,
        SavePromptRecordViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of the resolved workspace's prompt library, newest first.
    /// </summary>
    /// <param name="model">
    /// The filters, cursor and page size. Carries no workspace — see <see cref="PromptSearchViewModel"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>No role gate.</strong> <c>Viewer</c> is the lowest role, so a resolved workspace context already
    /// is the authorization — the same reasoning <c>IRecipeFacade.GetDetailAsync</c> records.
    /// </para>
    /// <para>
    /// <strong>Nothing is cached, and that is a decision.</strong> A workspace-private list that changes
    /// whenever a prompt is saved is a poor cache candidate, and a cache key missing its workspace prefix is
    /// the classic way a list leaks across the boundary. There is no cached value here to isolate.
    /// </para>
    /// </remarks>
    Task<OperationResult<PromptSearchPageServiceModel>> SearchAsync(
        PromptSearchViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one prompt of the resolved workspace in full: the whole text, the model's draft, and the template
    /// triple the list deliberately withholds.
    /// </summary>
    /// <param name="promptRecordId">
    /// The prompt to read. Never a workspace — the route's slug and the caller's membership resolved that
    /// already, and a prompt id is not a capability on its own: another workspace's id is refused in the same
    /// words an unknown one is.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>No role gate</strong>, and no validator: <c>Viewer</c> is the lowest role, so a resolved
    /// workspace context already is the authorization — the reasoning <c>IRecipeFacade.GetDetailAsync</c>
    /// records — and the route's <c>:guid</c> constraint is the only shape check a single id can fail.
    /// </para>
    /// <para>
    /// <strong>Nothing is cached</strong>, for the reason <see cref="SearchAsync"/> gives: workspace-private
    /// creator content, and a cache key written without its <c>workspace:{id}:</c> prefix is the classic way
    /// one creator's library reaches another. There is no cached value here to isolate, and
    /// <c>PromptDetailTests</c> keeps that true.
    /// </para>
    /// </remarks>
    Task<OperationResult<PromptDetailServiceModel>> GetDetailAsync(
        Guid promptRecordId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPromptRecordFacade"/>
internal sealed class PromptRecordFacade(
    IValidator<SavePromptRecordViewModel> saveValidator,
    IValidator<PromptSearchViewModel> searchValidator,
    IPromptRecordBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IPromptRecordFacade
{
    /// <summary>A stable operation name for the idempotency scope. Changing it orphans in-flight keys.</summary>
    private const string SaveOperation = "content.prompt_record.save";

    private const string CannotList = "That prompt list cannot be read as described.";

    public async Task<IdempotentOutcome<SavedPromptRecordServiceModel>> SaveAsync(
        string userId,
        SavePromptRecordViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Contributor: saving a prompt is contributing creator content, the same bar as creating a recipe. The
        // route policy has already refused a lower role; this is the backstop for a worker or plugin reaching
        // the facade directly, which is why the facade rather than the controller owns the bar.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused(new OperationError(
                ContentErrorCodes.PromptForbidden,
                "You do not have permission to save prompts in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await saveValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                ContentErrorCodes.PromptInvalid,
                "The prompt could not be saved.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, SaveOperation, idempotencyKey, Fingerprint(model), KeyRequired: false),
            token => business.SaveAsync(model, token),
            cancellationToken);
    }

    public async Task<OperationResult<PromptSearchPageServiceModel>> SearchAsync(
        PromptSearchViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await searchValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            // The cursor's own code when that is what failed, so a paging client knows to start again rather
            // than retrying a cursor that will never be accepted. The property names come from the validator's
            // OverridePropertyName, which is what keeps this matching the query parameter a caller sent.
            var code = validation.Errors.Any(failure =>
                failure.PropertyName.Equals("cursor", StringComparison.Ordinal))
                ? ContentErrorCodes.PromptCursorInvalid
                : ContentErrorCodes.PromptSearchInvalid;

            return OperationResult<PromptSearchPageServiceModel>.Failure(OperationError.Validation(
                code,
                CannotList,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The workspace comes from the resolved context, never from the model — which has no field for it. It is
        // bound into the cursor's scope so a cursor cannot be replayed across workspaces.
        if (!PromptSearchQueryFactory.TryCreate(model, workspace.WorkspaceId, out var criteria, out var error))
        {
            return OperationResult<PromptSearchPageServiceModel>.Failure(error!);
        }

        return OperationResult<PromptSearchPageServiceModel>.Success(
            await business.SearchAsync(criteria!, cancellationToken));
    }

    public Task<OperationResult<PromptDetailServiceModel>> GetDetailAsync(
        Guid promptRecordId, CancellationToken cancellationToken) =>
        business.GetDetailAsync(promptRecordId, cancellationToken);

    /// <summary>
    /// The submitted prompt is what makes two saves "the same" — including its text.
    /// </summary>
    /// <remarks>
    /// The whole model, as the brand profile's create does. The text is a prompt body and so is private creator
    /// content, which is acceptable here and only here: <see cref="IdempotentCommand.Fingerprint"/> is
    /// HMAC-hashed and the hash alone is stored, so no prompt text reaches the idempotency table. Nothing else
    /// about this operation may log or summarise it.
    /// </remarks>
    private static object Fingerprint(SavePromptRecordViewModel model) => model;

    private static IdempotentOutcome<SavedPromptRecordServiceModel> Refused(OperationError error) =>
        new(OperationResult<SavedPromptRecordServiceModel>.Failure(error), Replayed: false);
}
