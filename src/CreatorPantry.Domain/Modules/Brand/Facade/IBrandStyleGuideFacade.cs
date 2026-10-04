using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

public interface IBrandStyleGuideFacade
{
    /// <summary>
    /// Reads one guide's working version and, if it holds the workspace default, its active one. Any member of
    /// the workspace may read; an unknown guide and another workspace's are the same answer.
    /// </summary>
    Task<OperationResult<BrandStyleGuideDetailServiceModel>> GetAsync(Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of a guide's version history, newest version first. Any member of the workspace may read.
    /// Metadata only: no section, rule or body, and nothing is written.
    /// </summary>
    /// <param name="guideId">The guide, from the route. Never from the query string or the body.</param>
    /// <param name="model">Cursor and page size. Carries no workspace and no guide.</param>
    /// <remarks>
    /// Not cached. A page's key would have to carry the cursor, and no write to a version, an approval or the
    /// workspace default could then enumerate which pages to invalidate — the same reasoning as the source
    /// library's list.
    /// </remarks>
    Task<OperationResult<CursorPageServiceModel<BrandStyleGuideVersionSummaryServiceModel>>> ListVersionsAsync(
        Guid guideId, BrandStyleGuideVersionListViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Compares two of one guide's versions and returns what differs. Any member of the workspace may read.
    /// </summary>
    /// <param name="guideId">The guide, from the route. Never from the query string or the body.</param>
    /// <param name="model">Which two versions, by number. Carries no workspace and no guide.</param>
    /// <remarks>
    /// Read-only in the strongest sense: the comparison is calculated from two immutable versions, nothing is
    /// written, and no model is called. Not cached, for the reason the guide's other reads give.
    /// </remarks>
    Task<OperationResult<BrandStyleGuideVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid guideId, BrandStyleGuideVersionComparisonViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a guide and its version 1 from a questionnaire, structured sections and cited sources. Retryable
    /// with an idempotency key: a replay returns the guide the first request created.
    /// </summary>
    Task<IdempotentOutcome<BrandStyleGuideServiceModel>> CreateAsync(
        string userId,
        CreateBrandStyleGuideViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes one further version of a guide from a creator's own submitted edit (11A.15). Editor only.
    /// Retryable with an idempotency key: a replay returns the version the first request wrote.
    /// </summary>
    /// <param name="guideId">The guide, from the route. Never from the body.</param>
    /// <param name="model">
    /// The submitted change: the version it was made against, an optional reason, and the sections, rules and
    /// citations to set, clear or drop. Carries no workspace and no guide.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Editor</strong>, the role that creates a guide: this writes the creator's own brand voice.
    /// Approving and activating the version it writes remain separate decisions with their own gates.
    /// </para>
    /// <para>
    /// <strong>Wrapped in the idempotency executor, unlike <see cref="CreateVersionFromProposalAsync"/>.</strong>
    /// This is a top-level request with no transaction above it, so the executor's own is the transaction the
    /// working-version check needs to sit inside — and a creator's retry after a dropped response has no AI
    /// operation status to recognise itself by, which is exactly what a key is for.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<BrandStyleGuideVersionSavedServiceModel>> SaveVersionAsync(
        string userId,
        Guid guideId,
        SaveBrandStyleGuideVersionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Approves one version of one guide, which is what lets an Owner make it the workspace default. Editor
    /// only. Retryable with an idempotency key: a replay returns the approval the first request recorded.
    /// </summary>
    /// <param name="guideId">The guide, from the route. Never from the body.</param>
    /// <param name="versionNumber">Which of its versions, from the route. Never from the body.</param>
    /// <param name="model">The confirmation and an optional reason.</param>
    /// <remarks>
    /// <strong>Editor, the role that creates and edits a guide</strong>, and deliberately below the Owner that
    /// activation requires: saying a version is finished is the authoring decision, and choosing what the
    /// whole workspace writes with is not. Two roles for two decisions, which is also what lets an Editor
    /// hand finished work over without being able to repoint the workspace default themselves.
    /// </remarks>
    Task<IdempotentOutcome<BrandStyleGuideApprovalResultServiceModel>> ApproveVersionAsync(
        string userId,
        Guid guideId,
        int versionNumber,
        ApproveBrandStyleGuideVersionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes one approved version of one guide the workspace's default. Owner only. Retryable with an
    /// idempotency key: a replay returns the activation the first request recorded.
    /// </summary>
    /// <param name="guideId">The guide, from the route. Never from the body.</param>
    /// <param name="versionNumber">Which of its versions, from the route. Never from the body.</param>
    /// <param name="model">The confirmation, the expected current active version, and an optional reason.</param>
    Task<IdempotentOutcome<BrandStyleGuideActivationResultServiceModel>> ActivateVersionAsync(
        string userId,
        Guid guideId,
        int versionNumber,
        ActivateBrandStyleGuideVersionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes one further draft version of a guide from guidance a creator accepted out of an AI proposal
    /// (11A.18). Editor only.
    /// </summary>
    /// <param name="guideId">The guide, resolved by the caller from the proposal's own stored inputs. Never from a request body.</param>
    /// <param name="application">
    /// The accepted guidance in this module's vocabulary, with the working version it was composed against.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Not exposed over HTTP, and no view model.</strong> Its caller is the AI module's acceptance seam,
    /// which composes <see cref="BrandStyleGuideProposalApplication"/> from rows this server wrote and the
    /// creator's own rewrites. There is nothing here a client names: not the workspace, not the version number,
    /// not the citations.
    /// </para>
    /// <para>
    /// <strong>No idempotency wrapper, deliberately — and not an omission.</strong> This runs inside the
    /// transaction the caller has already opened, and <c>IIdempotentCommandExecutor</c> opens one of its own,
    /// which would throw on the nesting, and clears the change tracker, which would discard what the caller has
    /// staged. Replay is the caller's to recognise, from the AI operation's own terminal status, which is a
    /// stronger guarantee than a key: it survives the idempotency record expiring. This is the same split
    /// <c>IRecipeFacade.CreateFromProposalAsync</c> records against <c>CreateAsync</c>.
    /// </para>
    /// <para>
    /// <strong>Editor, the role that creates and edits a guide</strong> — above the Contributor who may ask for
    /// a proposal, because asking produces something to read and this produces a version of the creator's own
    /// brand voice. Checked here as well as by the caller: this boundary is reached by a worker and by an AI
    /// plugin, which no MVC policy protects.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandStyleGuideVersionCreatedServiceModel>> CreateVersionFromProposalAsync(
        string userId,
        Guid guideId,
        BrandStyleGuideProposalApplication application,
        CancellationToken cancellationToken);

    /// <summary>
    /// The guide version this workspace has made its default, in full, or <c>null</c> when it has none. Any
    /// member of the workspace may read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes no guide id, which is the whole point: a caller assembling brand context knows the workspace and
    /// not which of its guides holds the default. <see cref="GetAsync"/> answers the other question.
    /// </para>
    /// <para>
    /// <strong>Null is a success, and there is no fallback.</strong> A workspace that has activated nothing has
    /// no active guide; answering with the newest approved version instead would ground a generation on
    /// something the creator never chose, which is the hidden fallback 11A.19 forbids.
    /// </para>
    /// <para>
    /// Not cached, for the reason this facade's other reads give. No role gate beyond the caller having a
    /// resolved workspace: it is a read of the workspace's own guide.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandActiveStyleGuideServiceModel?>> GetActiveAsync(CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideFacade(
    IValidator<CreateBrandStyleGuideViewModel> validator,
    IValidator<BrandStyleGuideVersionComparisonViewModel> comparisonValidator,
    IValidator<ActivateBrandStyleGuideVersionViewModel> activationValidator,
    IValidator<ApproveBrandStyleGuideVersionViewModel> approvalValidator,
    IValidator<SaveBrandStyleGuideVersionViewModel> editValidator,
    IBrandStyleGuideBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IBrandStyleGuideFacade
{
    private const string CreateOperation = "brand.guide.create";

    private const string SaveVersionOperation = "brand.guide.version.save";

    private const string ApproveOperation = "brand.guide.approve";

    private const string ActivateOperation = "brand.guide.activate";

    public Task<OperationResult<BrandStyleGuideDetailServiceModel>> GetAsync(
        Guid guideId, CancellationToken cancellationToken) =>
        business.GetAsync(guideId, cancellationToken);

    public Task<OperationResult<BrandActiveStyleGuideServiceModel?>> GetActiveAsync(
        CancellationToken cancellationToken) =>
        business.GetActiveAsync(cancellationToken);

    public Task<OperationResult<CursorPageServiceModel<BrandStyleGuideVersionSummaryServiceModel>>> ListVersionsAsync(
        Guid guideId, BrandStyleGuideVersionListViewModel model, CancellationToken cancellationToken)
    {
        // The workspace comes from the resolved context and the guide from the route. The model has a field
        // for neither, and both are bound into the cursor's scope rather than trusted from it.
        if (!BrandStyleGuideVersionListQueryFactory.TryCreate(
                model, workspace.WorkspaceId, guideId, out var criteria, out var error))
        {
            return Task.FromResult(
                OperationResult<CursorPageServiceModel<BrandStyleGuideVersionSummaryServiceModel>>.Failure(error!));
        }

        // Every member may read, as for the single-guide read: no role gate beyond the route's policy.
        return business.ListVersionsAsync(criteria!, cancellationToken);
    }

    public async Task<OperationResult<BrandStyleGuideVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid guideId, BrandStyleGuideVersionComparisonViewModel model, CancellationToken cancellationToken)
    {
        var validation = await comparisonValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<BrandStyleGuideVersionComparisonServiceModel>.Failure(OperationError.Validation(
                BrandErrorCodes.GuideInvalidRequest,
                "Those versions could not be compared.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // Non-null past the validator, which requires both. The guide comes from the route; the workspace
        // reaches the query through the resolved context, and the model has a field for neither.
        return await business.CompareVersionsAsync(
            guideId, model.From!.Value, model.To!.Value, cancellationToken);
    }

    public async Task<IdempotentOutcome<BrandStyleGuideServiceModel>> CreateAsync(
        string userId,
        CreateBrandStyleGuideViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Editor, as for the brand profile and its source documents: the guide is drawn from them.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Refused(new OperationError(
                BrandErrorCodes.GuideForbidden,
                "You do not have permission to create brand style guides in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                BrandErrorCodes.GuideInvalidRequest,
                "The brand style guide could not be created.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var draft = BrandStyleGuideInput.Compose(model);

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(userId, workspace.WorkspaceId, CreateOperation, idempotencyKey, Fingerprint(draft)),
            token => business.CreateAsync(userId, draft, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<BrandStyleGuideVersionSavedServiceModel>> SaveVersionAsync(
        string userId,
        Guid guideId,
        SaveBrandStyleGuideVersionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Editor, as for creating a guide: this writes a version of the creator's own brand voice.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return RefusedSave(new OperationError(
                BrandErrorCodes.GuideForbidden,
                "You do not have permission to edit brand style guides in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await editValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return RefusedSave(OperationError.Validation(
                BrandErrorCodes.GuideInvalidRequest,
                "This change could not be saved.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var edit = BrandStyleGuideEditInput.Compose(model);

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, SaveVersionOperation, idempotencyKey, Fingerprint(guideId, edit)),
            token => business.SaveVersionAsync(userId, guideId, edit, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<BrandStyleGuideApprovalResultServiceModel>> ApproveVersionAsync(
        string userId,
        Guid guideId,
        int versionNumber,
        ApproveBrandStyleGuideVersionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Editor, as for creating and editing a guide: approving says the authoring is finished. Activation
        // asks for Owner because it repoints what every later generation is grounded on, which is a different
        // decision and stays a different gate.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return RefusedApproval(new OperationError(
                BrandErrorCodes.GuideForbidden,
                "You do not have permission to approve brand style guide versions in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await approvalValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return RefusedApproval(OperationError.Validation(
                BrandErrorCodes.GuideInvalidRequest,
                "This brand style guide version could not be approved.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                ApproveOperation,
                idempotencyKey,
                Fingerprint(guideId, versionNumber, model)),
            token => business.ApproveVersionAsync(userId, guideId, versionNumber, model.Reason, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<BrandStyleGuideActivationResultServiceModel>> ActivateVersionAsync(
        string userId,
        Guid guideId,
        int versionNumber,
        ActivateBrandStyleGuideVersionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Owner, above the Editor that creates and edits a guide. Activation is the workspace's one
        // brand-voice decision and it governs what every later generation is grounded on, so it sits with the
        // operations auth.md keeps behind an explicit policy and an audit event rather than with authoring.
        if (workspace.Role < WorkspaceRole.Owner)
        {
            return RefusedActivation(new OperationError(
                BrandErrorCodes.GuideForbidden,
                "You do not have permission to change this workspace's default brand style guide.",
                new Dictionary<string, string[]>()));
        }

        var validation = await activationValidator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return RefusedActivation(OperationError.Validation(
                BrandErrorCodes.GuideInvalidRequest,
                "This brand style guide version could not be activated.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                ActivateOperation,
                idempotencyKey,
                Fingerprint(guideId, versionNumber, model)),
            token => business.ActivateVersionAsync(
                userId, guideId, versionNumber, model.ExpectedActiveVersionId, model.Reason, token),
            cancellationToken);
    }

    public async Task<OperationResult<BrandStyleGuideVersionCreatedServiceModel>> CreateVersionFromProposalAsync(
        string userId,
        Guid guideId,
        BrandStyleGuideProposalApplication application,
        CancellationToken cancellationToken)
    {
        // Editor, as for creating a guide: this writes a version of it. Authorization first, so a caller who may
        // not do this learns that rather than which of their citations failed to resolve.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return OperationResult<BrandStyleGuideVersionCreatedServiceModel>.Failure(new OperationError(
                BrandErrorCodes.GuideForbidden,
                "You do not have permission to write brand style guide versions in this workspace.",
                new Dictionary<string, string[]>()));
        }

        // No validator: the application is composed server-side from stored rows, so there is no request shape
        // to check. Its bounds are this module's invariants, and Business owns those.
        return await business.CreateVersionFromProposalAsync(userId, guideId, application, cancellationToken);
    }

    /// <summary>
    /// What makes two activations the same request: the version named, the state expected, and the reason
    /// recorded.
    /// </summary>
    /// <remarks>
    /// <c>confirmed</c> is left out deliberately. It is the only value here a request cannot vary and still
    /// be accepted — the validator refuses anything but <c>true</c> — so including it could not distinguish
    /// two requests, and leaving it out keeps the fingerprint to the things that actually differ.
    /// </remarks>
    private static object Fingerprint(
        Guid guideId, int versionNumber, ActivateBrandStyleGuideVersionViewModel model) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["guideId"] = guideId,
            ["versionNumber"] = versionNumber,
            ["expectedActiveVersionId"] = model.ExpectedActiveVersionId,
            ["reason"] = model.Reason,
        };

    private static IdempotentOutcome<BrandStyleGuideActivationResultServiceModel> RefusedActivation(
        OperationError error) =>
        new(OperationResult<BrandStyleGuideActivationResultServiceModel>.Failure(error), Replayed: false);

    /// <summary>
    /// What an approval's replay is recognised by: the version named and the words stored with it.
    /// </summary>
    /// <remarks>
    /// <c>confirmed</c> is left out for the reason the activation fingerprint gives: the validator accepts
    /// only <c>true</c>, so it cannot distinguish two requests.
    /// </remarks>
    private static object Fingerprint(
        Guid guideId, int versionNumber, ApproveBrandStyleGuideVersionViewModel model) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["guideId"] = guideId,
            ["versionNumber"] = versionNumber,
            ["reason"] = model.Reason,
        };

    private static IdempotentOutcome<BrandStyleGuideApprovalResultServiceModel> RefusedApproval(
        OperationError error) =>
        new(OperationResult<BrandStyleGuideApprovalResultServiceModel>.Failure(error), Replayed: false);

    /// <summary>
    /// What makes two saves the same request: the guide, the version the edit was made against, and every part
    /// of the change itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The composed edit rather than the request, as on creation, so two requests differing only in blanks are
    /// one request — and so a retry that re-serialises the same change in a different shape is still a replay.
    /// </para>
    /// <para>
    /// <c>expectedWorkingVersionNumber</c> is in it deliberately. The same words saved against version 3 and
    /// against version 4 are two different edits, and a key reused across them has to be refused rather than
    /// answered with the first one's version.
    /// </para>
    /// <para>
    /// A null rule list is distinguished from an empty one: leaving rules alone and clearing every rule are not
    /// the same request, and a fingerprint that flattened both to "no rules" would let one replay as the other.
    /// </para>
    /// </remarks>
    private static object Fingerprint(Guid guideId, BrandStyleGuideEditDraft edit) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["guideId"] = guideId,
            ["expectedWorkingVersionNumber"] = edit.ExpectedWorkingVersionNumber,
            ["changeReason"] = edit.ChangeReason,
            ["sections"] = edit.Sections
                .Select(section => new object?[] { section.SectionKey, section.ChannelKey, section.Body })
                .ToArray(),
            ["rules"] = edit.Rules?.Select(rule => new object?[] { rule.Kind, rule.Text }).ToArray(),
            ["cite"] = edit.Cite.Select(source => new object?[] { source.DocumentId, source.VersionNumber }).ToArray(),
            ["uncite"] = edit.Uncite.Select(source => new object?[] { source.DocumentId, source.VersionNumber }).ToArray(),
        };

    private static IdempotentOutcome<BrandStyleGuideVersionSavedServiceModel> RefusedSave(OperationError error) =>
        new(OperationResult<BrandStyleGuideVersionSavedServiceModel>.Failure(error), Replayed: false);

    // The draft rather than the request, so two requests that differ only in blanks are the same request.
    private static object Fingerprint(BrandStyleGuideDraft draft) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["displayName"] = draft.DisplayName,
            ["purpose"] = draft.Purpose,
            ["sections"] = draft.Sections.Select(section => new object?[] { section.SectionKey, section.ChannelKey, section.Body }).ToArray(),
            ["rules"] = draft.Rules.Select(rule => new object?[] { rule.Kind, rule.Text }).ToArray(),
            ["sources"] = draft.Sources.Select(source => new object?[] { source.DocumentId, source.VersionNumber }).ToArray(),
        };

    private static IdempotentOutcome<BrandStyleGuideServiceModel> Refused(OperationError error) =>
        new(OperationResult<BrandStyleGuideServiceModel>.Failure(error), Replayed: false);
}
