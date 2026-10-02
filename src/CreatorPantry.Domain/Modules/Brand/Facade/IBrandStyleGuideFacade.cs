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
}

internal sealed class BrandStyleGuideFacade(
    IValidator<CreateBrandStyleGuideViewModel> validator,
    IValidator<BrandStyleGuideVersionComparisonViewModel> comparisonValidator,
    IValidator<ActivateBrandStyleGuideVersionViewModel> activationValidator,
    IBrandStyleGuideBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IBrandStyleGuideFacade
{
    private const string CreateOperation = "brand.guide.create";

    private const string ActivateOperation = "brand.guide.activate";

    public Task<OperationResult<BrandStyleGuideDetailServiceModel>> GetAsync(
        Guid guideId, CancellationToken cancellationToken) =>
        business.GetAsync(guideId, cancellationToken);

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
