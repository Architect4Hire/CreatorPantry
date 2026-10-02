using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiBrandGuideProposalRequestBusiness
{
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestBrandGuideProposalViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Queues a brand-guide proposal for one of the workspace's style guides (11A.17), through the same operation
/// lifecycle as every other AI task.
/// </summary>
/// <remarks>
/// <para>
/// Reads the guide through <see cref="IBrandStyleGuideFacade"/> and the selected documents through
/// <see cref="IBrandSourceDocumentFacade"/>, never their repositories — cross-module traffic is facade to facade
/// (backend.md).
/// </para>
/// <para>
/// <strong>What is pinned here and why.</strong> The guide's working version number, so the handler can refuse
/// to run against answers that have since been rewritten; and each selected document's current version number,
/// so the proposal records the exact text it was grounded in even after the document is replaced. Both are
/// resolved server-side: the request names a guide and some documents, never a version.
/// </para>
/// <para>
/// <strong>What is refused here rather than warned about later.</strong> A document that does not resolve at all
/// is the caller's mistake and is answered immediately, with one code for every cause. A document that resolves
/// but has no indexed passages yet is not a mistake — its text may still be extracting — so it travels and the
/// proposal's own findings say the evidence was thin.
/// </para>
/// </remarks>
internal sealed class AiBrandGuideProposalRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IBrandStyleGuideFacade guides,
    IBrandSourceDocumentFacade documents,
    IContentChannelCatalog channels,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiBrandGuideProposalRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestBrandGuideProposalViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.BrandGuideProposal))
        {
            return Refuse(
                AiBrandGuideProposalRequestErrors.TaskNotEnabled,
                "Brand guide proposals are not enabled for this workspace's deployment.");
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.BrandGuideProposal, cancellationToken) is { } spent)
        {
            return spent;
        }

        var dimensions = Dimensions(model);

        if (Channels(model, dimensions) is not { } channelKeys)
        {
            return Refuse(
                AiBrandGuideProposalRequestErrors.ChannelInvalid,
                dimensions.Contains(AiBrandGuideDimension.Channel)
                    ? "Name at least one channel this product knows to write channel guidance for."
                    : "Channel keys are only used when channel guidance is asked for.");
        }

        var guide = await guides.GetAsync(model.GuideId, cancellationToken);

        if (!guide.Succeeded)
        {
            // Answered identically to a guide that was never created (tenancy.md).
            return Refuse(AiBrandGuideProposalRequestErrors.GuideNotFound, "That brand style guide does not exist.");
        }

        if (await PinSourcesAsync(model.SourceDocumentIds, cancellationToken) is not { } sources)
        {
            return Refuse(
                AiBrandGuideProposalRequestErrors.SourceUnprocessable,
                "A selected source document could not be used.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.BrandGuideProposal,

                // Not taken from the request, and NotApplicable rather than Advisory: this task names no recipe
                // at all, so there is nothing for a change to address and nothing for staleness to measure.
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,

                // A brand guide is not a recipe. Both stay null, as they do for a concept or a first draft.
                RecipeId = null,
                RecipeVersionId = null,
                TaskInputsJson = SerializeInputs(
                    model.GuideId, guide.Value!.WorkingVersion.VersionNumber, dimensions, channelKeys, sources),
                IdempotencyKey = idempotencyKey,
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);

        if (requested.Outcome is AiOperationRequestOutcome.KeyReusedForDifferentRequest)
        {
            return Refuse(IdempotencyPolicy.KeyReusedCode, "That idempotency key was already used for a different request.");
        }

        return new IdempotentOutcome<AiProposalStatusServiceModel>(
            OperationResult<AiProposalStatusServiceModel>.Success(AiOperationDescription.Describe(requested.Operation!, null)),
            Replayed: requested.Outcome is AiOperationRequestOutcome.Replayed);
    }

    public async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // One absence for three conditions: no such request, one in another workspace, and one that ran a
        // different task.
        if (operation is null || operation.Operation.TaskType != AiTaskType.BrandGuideProposal)
        {
            return Failure(
                AiBrandGuideProposalRequestErrors.RequestNotFound, "That brand guide proposal request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// The dimensions asked for, or every one but <see cref="AiBrandGuideDimension.Channel"/> when none was
    /// named.
    /// </summary>
    /// <remarks>
    /// Channel is excluded from the default deliberately: it is the one dimension that needs a second field to
    /// mean anything, so including it would turn an empty request into a refusal about a field the caller never
    /// mentioned.
    /// </remarks>
    private static IReadOnlySet<AiBrandGuideDimension> Dimensions(RequestBrandGuideProposalViewModel model) =>
        model.Dimensions is { Count: > 0 } named
            ? named.Select(name => AiBrandGuideDimensionCatalog.Parse(name)!.Value).ToHashSet()
            :
            [
                .. Enum.GetValues<AiBrandGuideDimension>()
                    .Where(dimension => dimension is not AiBrandGuideDimension.Unspecified
                        and not AiBrandGuideDimension.Channel),
            ];

    /// <summary>
    /// The channel keys to offer, or null when the request and the dimensions disagree about them.
    /// </summary>
    /// <remarks>
    /// Checked against the catalogue, so a key the product does not know is refused here rather than handed to a
    /// model that would invent that channel's conventions. Retired channels are accepted — a creator may still
    /// hold guidance for one — because the catalogue knows them and the key is identity, not availability.
    /// </remarks>
    private IReadOnlySet<string>? Channels(
        RequestBrandGuideProposalViewModel model, IReadOnlySet<AiBrandGuideDimension> dimensions)
    {
        var requested = model.ChannelKeys ?? [];

        if (!dimensions.Contains(AiBrandGuideDimension.Channel))
        {
            // Keys with no channel dimension to apply to would be silently ignored, which is worse than refused:
            // a creator who named channels expects guidance for them.
            return requested.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) : null;
        }

        return requested.Count > 0 && requested.All(key => channels.Find(key) is not null)
            ? requested.ToHashSet(StringComparer.Ordinal)
            : null;
    }

    /// <summary>
    /// Each selected document's current version, or null when any selection does not resolve.
    /// </summary>
    /// <remarks>
    /// All or nothing, deliberately. Dropping the one that failed would ground the proposal on less than the
    /// creator chose while the stored inputs claimed otherwise, and they would have no way to tell.
    /// </remarks>
    private async Task<IReadOnlyList<(Guid DocumentId, int VersionNumber)>?> PinSourcesAsync(
        IReadOnlyList<Guid>? documentIds, CancellationToken cancellationToken)
    {
        if (documentIds is null or { Count: 0 })
        {
            return [];
        }

        var pinned = new List<(Guid, int)>(documentIds.Count);

        foreach (var documentId in documentIds.Distinct())
        {
            var document = await documents.GetAsync(documentId, cancellationToken);

            if (!document.Succeeded)
            {
                return null;
            }

            pinned.Add((documentId, document.Value!.CurrentVersion.VersionNumber));
        }

        return pinned;
    }

    private static string SerializeInputs(
        Guid guideId,
        int guideVersionNumber,
        IReadOnlySet<AiBrandGuideDimension> dimensions,
        IReadOnlySet<string> channelKeys,
        IReadOnlyList<(Guid DocumentId, int VersionNumber)> sources)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AiBrandGuideProposalInputs.GuideId] = guideId.ToString("D"),

            // The version the creator's answers were read from. The handler refuses to run against any other.
            [AiBrandGuideProposalInputs.GuideVersionNumber] =
                guideVersionNumber.ToString(CultureInfo.InvariantCulture),
            [AiBrandGuideProposalInputs.Dimensions] = string.Join(
                ',', dimensions.Select(AiBrandGuideDimensionCatalog.ToWire).Order(StringComparer.Ordinal)),
        };

        if (channelKeys.Count > 0)
        {
            values[AiBrandGuideProposalInputs.ChannelKeys] =
                string.Join(',', channelKeys.Order(StringComparer.Ordinal));
        }

        if (sources.Count > 0)
        {
            // Ordered, so two requests naming the same documents in a different order are one request to the
            // idempotency reconciler rather than two.
            values[AiBrandGuideProposalInputs.SourceVersions] = string.Join(
                ',',
                sources
                    .OrderBy(source => source.DocumentId)
                    .Select(source => $"{source.DocumentId:N}:{source.VersionNumber.ToString(CultureInfo.InvariantCulture)}"));
        }

        return JsonSerializer.Serialize(values);
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(new OperationError(code, message, new Dictionary<string, string[]>()));
}
