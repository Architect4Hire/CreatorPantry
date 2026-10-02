using CreatorPantry.Domain.Managers.Idempotency;
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
    /// Creates a guide and its version 1 from a questionnaire, structured sections and cited sources. Retryable
    /// with an idempotency key: a replay returns the guide the first request created.
    /// </summary>
    Task<IdempotentOutcome<BrandStyleGuideServiceModel>> CreateAsync(
        string userId,
        CreateBrandStyleGuideViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideFacade(
    IValidator<CreateBrandStyleGuideViewModel> validator,
    IBrandStyleGuideBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IBrandStyleGuideFacade
{
    private const string CreateOperation = "brand.guide.create";

    public Task<OperationResult<BrandStyleGuideDetailServiceModel>> GetAsync(
        Guid guideId, CancellationToken cancellationToken) =>
        business.GetAsync(guideId, cancellationToken);

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
