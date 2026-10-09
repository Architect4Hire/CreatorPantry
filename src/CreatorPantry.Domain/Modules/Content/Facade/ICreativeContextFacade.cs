using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;
using FluentValidation.Results;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The application boundary for creative contexts (AF.1.3): what one piece of creative work is about, shared
/// by every AI surface that works on it.
/// </summary>
/// <remarks>
/// The workspace is never an argument. It reaches every operation through the resolved
/// <see cref="IWorkspaceContext"/>, so a controller, a worker and a plugin all get the same isolation, and no
/// view model here has a field one could arrive in.
/// </remarks>
public interface ICreativeContextFacade
{
    /// <summary>Starts a context, optionally from one source. Contributor and above.</summary>
    /// <remarks>
    /// Idempotent under an optional key: replaying the key with the same body returns the context the first
    /// call created, and with a different body is refused.
    /// </remarks>
    Task<IdempotentOutcome<CreativeContextServiceModel>> CreateAsync(
        string userId,
        CreateCreativeContextViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Reads one context in full. Any member.</summary>
    Task<OperationResult<CreativeContextServiceModel>> GetAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>Lists live contexts, most recently updated first. Any member.</summary>
    Task<OperationResult<CursorPageServiceModel<CreativeContextSummaryServiceModel>>> ListRecentAsync(
        CreativeContextListViewModel model, CancellationToken cancellationToken);

    /// <summary>Edits the creator's words, channels, day, theme or archived state. Contributor and above.</summary>
    Task<OperationResult<CreativeContextServiceModel>> PatchAsync(
        string userId, Guid contextId, PatchCreativeContextViewModel model, CancellationToken cancellationToken);

    /// <summary>Names one more source, after those already named. Contributor and above.</summary>
    Task<OperationResult<CreativeContextServiceModel>> AddReferenceAsync(
        Guid contextId, AddCreativeContextReferenceViewModel model, CancellationToken cancellationToken);

    /// <summary>Stops naming a source. The source itself is untouched. Contributor and above.</summary>
    Task<OperationResult<CreativeContextServiceModel>> RemoveReferenceAsync(
        Guid contextId, Guid referenceId, string? expectedConcurrencyToken, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextFacade"/>
internal sealed class CreativeContextFacade(
    IValidator<CreateCreativeContextViewModel> createValidator,
    IValidator<PatchCreativeContextViewModel> patchValidator,
    IValidator<AddCreativeContextReferenceViewModel> addValidator,
    IValidator<CreativeContextListViewModel> listValidator,
    ICreativeContextBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : ICreativeContextFacade
{
    private const string CreateOperation = "content.creative_context.create";

    private const string CannotSave = "That creative context could not be saved as described.";

    public async Task<IdempotentOutcome<CreativeContextServiceModel>> CreateAsync(
        string userId,
        CreateCreativeContextViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return new(Failure(forbidden), Replayed: false);
        }

        if (Invalid(await createValidator.ValidateAsync(model, cancellationToken)) is { } invalid)
        {
            return new(Failure(invalid), Replayed: false);
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, CreateOperation, idempotencyKey, model, KeyRequired: false),
            token => business.CreateAsync(userId, model, token),
            cancellationToken);
    }

    public Task<OperationResult<CreativeContextServiceModel>> GetAsync(
        Guid contextId, CancellationToken cancellationToken) =>
        business.GetAsync(contextId, cancellationToken);

    public async Task<OperationResult<CursorPageServiceModel<CreativeContextSummaryServiceModel>>> ListRecentAsync(
        CreativeContextListViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await listValidator.ValidateAsync(model, cancellationToken);

        // The workspace comes from the resolved context, never from the model — which has no field for it. It
        // is bound into the cursor's scope so a cursor cannot be replayed across workspaces.
        var scope = CreativeContextListCriteria.ScopeFor(workspace.WorkspaceId);

        CreativeContextListPosition? position = null;

        if (!validation.IsValid
            || !ReferenceCursor.TryResolve(model.Cursor, scope, out var cursor)
            || (cursor is not null && !CreativeContextListPosition.TryCreate(cursor, out position)))
        {
            return OperationResult<CursorPageServiceModel<CreativeContextSummaryServiceModel>>.Failure(
                OperationError.Validation(
                    ContentErrorCodes.CreativeContextCursorInvalid,
                    "This cursor was not issued for this workspace's list. Start again without one.",
                    [("cursor", "Start the list again without a cursor.")]));
        }

        return OperationResult<CursorPageServiceModel<CreativeContextSummaryServiceModel>>.Success(
            await business.ListRecentAsync(
                new CreativeContextListCriteria(scope, position, model.Limit), cancellationToken));
    }

    public async Task<OperationResult<CreativeContextServiceModel>> PatchAsync(
        string userId, Guid contextId, PatchCreativeContextViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return Failure(forbidden);
        }

        if (MalformedToken(model.ExpectedConcurrencyToken) is { } malformed)
        {
            return Failure(malformed);
        }

        if (Invalid(await patchValidator.ValidateAsync(model, cancellationToken)) is { } invalid)
        {
            return Failure(invalid);
        }

        return await business.PatchAsync(userId, contextId, model, cancellationToken);
    }

    public async Task<OperationResult<CreativeContextServiceModel>> AddReferenceAsync(
        Guid contextId, AddCreativeContextReferenceViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return Failure(forbidden);
        }

        if (MalformedToken(model.ExpectedConcurrencyToken) is { } malformed)
        {
            return Failure(malformed);
        }

        if (Invalid(await addValidator.ValidateAsync(model, cancellationToken)) is { } invalid)
        {
            return Failure(invalid);
        }

        return await business.AddReferenceAsync(contextId, model, cancellationToken);
    }

    public Task<OperationResult<CreativeContextServiceModel>> RemoveReferenceAsync(
        Guid contextId, Guid referenceId, string? expectedConcurrencyToken, CancellationToken cancellationToken)
    {
        if (Forbidden() is { } forbidden)
        {
            return Task.FromResult(Failure(forbidden));
        }

        if (MalformedToken(expectedConcurrencyToken) is { } malformed)
        {
            return Task.FromResult(Failure(malformed));
        }

        return business.RemoveReferenceAsync(contextId, referenceId, expectedConcurrencyToken, cancellationToken);
    }

    // Contributor: shaping a piece of work is contributing creator content, the same bar as saving a prompt.
    // The route policy has already refused a lower role; this is the backstop for a worker or plugin reaching
    // the facade directly, which is why the facade rather than the controller owns the bar.
    private OperationError? Forbidden() =>
        workspace.Role < WorkspaceRole.Contributor
            ? new OperationError(
                ContentErrorCodes.CreativeContextForbidden,
                "You do not have permission to change creative contexts in this workspace.",
                new Dictionary<string, string[]>())
            : null;

    // Shape, so 400 rather than 409: a token that could never have been issued is not a stale one, and telling
    // a client to re-read would send it round a loop that cannot end differently.
    private static OperationError? MalformedToken(string? token) =>
        CreativeContextConcurrencyToken.IsWellFormed(token)
            ? null
            : OperationError.Validation(
                ContentErrorCodes.CreativeContextInvalid,
                CannotSave,
                [("expectedConcurrencyToken", "Send the concurrencyToken from the read this edit was composed against.")]);

    private static OperationError? Invalid(ValidationResult validation) =>
        validation.IsValid
            ? null
            : OperationError.Validation(
                ContentErrorCodes.CreativeContextInvalid,
                CannotSave,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage)));

    private static OperationResult<CreativeContextServiceModel> Failure(OperationError error) =>
        OperationResult<CreativeContextServiceModel>.Failure(error);
}
