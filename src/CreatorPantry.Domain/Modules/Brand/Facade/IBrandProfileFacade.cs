using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Facade;

/// <summary>
/// The application boundary for the workspace brand profile: read, create and update. There is no delete.
/// </summary>
public interface IBrandProfileFacade
{
    /// <summary>
    /// The resolved workspace's brand profile, or <c>brand.profile.not_found</c> when it has none.
    /// </summary>
    Task<OperationResult<BrandProfileServiceModel>> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the profile. Editor or above. Every logo in <c>assets</c> must be an asset in this workspace's
    /// library: an unknown one, another workspace's and one removed from the library are refused alike.
    /// </summary>
    Task<IdempotentOutcome<BrandProfileServiceModel>> CreateAsync(
        string userId, CreateBrandProfileViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a merge patch quoting the concurrency token of the read it was composed against. Editor or above.
    /// A submitted <c>assets</c> list replaces the profile's logos; unlinking one removes the link and nothing
    /// about the asset.
    /// </summary>
    Task<IdempotentOutcome<BrandProfileServiceModel>> UpdateAsync(
        string userId, UpdateBrandProfileViewModel model, string? idempotencyKey, CancellationToken cancellationToken);
}

internal sealed class BrandProfileFacade(
    IValidator<CreateBrandProfileViewModel> createValidator,
    IValidator<UpdateBrandProfileViewModel> updateValidator,
    IBrandProfileBusiness business,
    IMediaAssetLookupFacade mediaAssets,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IBrandProfileFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string CreateOperation = "brand.profile.create";

    private const string UpdateOperation = "brand.profile.update";

    private const string CannotSave = "The brand profile could not be saved.";

    public Task<OperationResult<BrandProfileServiceModel>> GetAsync(CancellationToken cancellationToken) =>
        business.GetAsync(cancellationToken);

    public async Task<IdempotentOutcome<BrandProfileServiceModel>> CreateAsync(
        string userId, CreateBrandProfileViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Forbidden() is { } forbidden)
        {
            return forbidden;
        }

        var validation = await createValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(Invalid(validation));
        }

        var linkable = await LinkableAsync(model.Assets, cancellationToken);

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, CreateOperation, idempotencyKey, Fingerprint(model), KeyRequired: false),
            token => business.CreateAsync(userId, model, linkable, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<BrandProfileServiceModel>> UpdateAsync(
        string userId, UpdateBrandProfileViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Forbidden() is { } forbidden)
        {
            return forbidden;
        }

        var validation = await updateValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(Invalid(validation));
        }

        var linkable = await LinkableAsync(
            model.Assets.TryGetSubmitted(out var assets) ? assets : null, cancellationToken);

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, UpdateOperation, idempotencyKey, Fingerprint(model), KeyRequired: false),
            token => business.UpdateAsync(userId, model, linkable, token),
            cancellationToken);
    }

    /// <summary>
    /// Which of the submitted logos this workspace can link, asked of the Media module through its facade.
    /// </summary>
    /// <remarks>
    /// Resolved here and handed to Business as a value, the way a recipe link's target is: cross-module traffic
    /// is facade to facade, and Business never reaches into another module (backend.md). The lookup runs in
    /// the resolved workspace, so another workspace's asset is simply not found — and a removed one is not
    /// either. Business is what decides which of those absences is a refusal, because it alone knows which
    /// links the profile already holds.
    /// </remarks>
    private async Task<IReadOnlySet<Guid>> LinkableAsync(
        IReadOnlyList<BrandAssetInput?>? assets, CancellationToken cancellationToken)
    {
        var linkable = new HashSet<Guid>();

        // Shape validation has already capped the list and refused repeats, so this is at most a handful of reads.
        foreach (var id in (assets ?? []).Select(item => item?.MediaAssetId).OfType<Guid>().Distinct())
        {
            if (await mediaAssets.ResolveLinkTargetAsync(id, versionNumber: null, cancellationToken) == MediaAssetLinkTarget.Linkable)
            {
                linkable.Add(id);
            }
        }

        return linkable;
    }

    // Editor, the same bar as archiving or restoring a recipe: the brand profile is shared identity that every
    // piece of work in the workspace leans on, so it costs more than contributing a draft.
    private IdempotentOutcome<BrandProfileServiceModel>? Forbidden() =>
        workspace.Role < WorkspaceRole.Editor
            ? Refused(new OperationError(
                BrandErrorCodes.Forbidden,
                "You do not have permission to change the brand profile in this workspace.",
                new Dictionary<string, string[]>()))
            : null;

    private static OperationError Invalid(FluentValidation.Results.ValidationResult validation) =>
        OperationError.Validation(
            BrandErrorCodes.InvalidRequest,
            CannotSave,
            validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage)));

    // The submitted fields and nothing else: the same edit against a different state of the profile is a
    // different request, so the expected token is part of the update's fingerprint.
    private static object Fingerprint(CreateBrandProfileViewModel model) => model;

    private static object Fingerprint(UpdateBrandProfileViewModel model)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["expectedConcurrencyToken"] = model.ExpectedConcurrencyToken,
            ["reason"] = model.Reason,
        };

        Add("brandName", model.BrandName);
        Add("shortDescription", model.ShortDescription);
        Add("defaultAudience", model.DefaultAudience);
        Add("locale", model.Locale);
        Add("timeZoneId", model.TimeZoneId);
        Add("channelDefaults", model.ChannelDefaults);
        Add("links", model.Links);
        Add("assets", model.Assets);

        return fields;

        void Add<T>(string name, CreatorPantry.Domain.Managers.Patching.PatchField<T> field)
        {
            if (field.TryGetSubmitted(out var value))
            {
                fields[name] = value;
            }
        }
    }

    private static IdempotentOutcome<BrandProfileServiceModel> Refused(OperationError error) =>
        new(OperationResult<BrandProfileServiceModel>.Failure(error), Replayed: false);
}
