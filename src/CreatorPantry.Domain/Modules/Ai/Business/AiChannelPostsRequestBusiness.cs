using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiChannelPostsRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestChannelPostsViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The request's status and the context's post package.</summary>
    Task<OperationResult<ChannelPostRequestStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around asking for posts (AF.6.4): that the task is enabled and affordable, that the piece of work
/// exists in this workspace, and that each named channel is one a post may be started for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it does not do.</strong> It does not assemble the context package, read the brand guide, or
/// look at a recipe — all of that is the handler's at the moment it calls the provider, and doing it here would
/// only let it go stale between the request and the call. It reads the post package through
/// <see cref="ISocialPackageFacade"/>, never the content module's repositories.
/// </para>
/// <para>
/// <strong>The package read does two jobs.</strong> It is how a context this workspace does not have becomes a
/// 404 before anything is queued, and it is how a retired channel is told apart from a retired channel that
/// already has a post — the first cannot be started, the second can be regenerated for ever.
/// </para>
/// </remarks>
internal sealed class AiChannelPostsRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    ISocialPackageFacade posts,
    IContentChannelCatalog channels,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiChannelPostsRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestChannelPostsViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.ChannelPosts))
        {
            return Refuse(
                AiChannelPostsRequestErrors.TaskNotEnabled,
                "Writing posts is not enabled for this workspace's deployment.");
        }

        // Before the operation is written, so a refused request leaves no orphan behind and spends no
        // allowance to say the allowance is spent (USAGE-006).
        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.ChannelPosts, cancellationToken) is { } spent)
        {
            return spent;
        }

        var package = await posts.GetAsync(model.CreativeContextId, cancellationToken);

        // One absence for "no such context" and for "that context is another workspace's": the content module
        // answers both the same way and this seam does not widen it.
        if (!package.Succeeded)
        {
            return Refuse(
                AiChannelPostsRequestErrors.ContextNotFound, "That piece of work does not exist.");
        }

        var written = package.Value?.Channels.Select(channel => channel.ChannelKey).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var requested = model.ChannelKeys!.Select(key => key.Trim()).ToList();

        // A retired channel may be written for again but not started. The validator has already refused a key
        // that names nothing at all, so a key the catalogue calls inactive is the only case left here.
        if (requested.FirstOrDefault(key =>
                channels.Find(key) is { IsActive: false } && !written.Contains(key)) is { } retired)
        {
            return Refuse(
                AiChannelPostsRequestErrors.ChannelRetired,
                $"The {retired} channel has been retired, so a first post cannot be written for it.");
        }

        var now = clock.UtcNow;

        var queued = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.ChannelPosts,

                // Not taken from the request, and NotApplicable rather than Advisory: the operation names no
                // recipe, and the scope is what makes "no stored row here has a path to a recipe edit" a
                // property of the row rather than a promise in a comment.
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,

                // Null on both. The subject is the piece of work; whichever recipe it pins is the context
                // package's to report, and pinning one here would be a second answer to the same question.
                RecipeId = null,
                RecipeVersionId = null,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = SerializeInputs(model.CreativeContextId, requested),
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);

        if (queued.Outcome is AiOperationRequestOutcome.KeyReusedForDifferentRequest)
        {
            return Refuse(
                IdempotencyPolicy.KeyReusedCode,
                "That idempotency key was already used for a different request.");
        }

        return new IdempotentOutcome<AiProposalStatusServiceModel>(
            OperationResult<AiProposalStatusServiceModel>.Success(
                AiOperationDescription.Describe(queued.Operation!, null)),
            Replayed: queued.Outcome is AiOperationRequestOutcome.Replayed);
    }

    public async Task<OperationResult<ChannelPostRequestStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // Three conditions, one absence: no such request, one in another workspace (the query filter removed
        // it before this read saw it), and one that ran a different task.
        if (operation is null || operation.Operation.TaskType != AiTaskType.ChannelPosts)
        {
            return OperationResult<ChannelPostRequestStatusServiceModel>.Failure(new OperationError(
                AiChannelPostsRequestErrors.RequestNotFound,
                "That post request does not exist.",
                new Dictionary<string, string[]>()));
        }

        var contextId = ChannelPostsInputs.ReadContextId(
            DeserializeInputs(operation.Operation.TaskInputsJson));

        // A read, and only a read. The generated bodies become revisions in the worker, immediately after the
        // proposal commits — polling a status must not be what writes a creator's content, not least because a
        // Viewer may poll and may not write.
        var package = contextId is { } id ? await posts.GetAsync(id, cancellationToken) : null;

        return OperationResult<ChannelPostRequestStatusServiceModel>.Success(
            new ChannelPostRequestStatusServiceModel(
                AiOperationDescription.Describe(operation.Operation, operation.Proposal),
                package?.Succeeded is true ? package.Value : null));
    }

    /// <summary>
    /// The request as the two inputs <see cref="ChannelPostsAiTaskHandler"/> reads back.
    /// </summary>
    /// <remarks>
    /// Keys trimmed, order kept. The whole string is what an idempotency replay is compared against
    /// (<c>AiTaskInputsIdentity</c>), and every character of it is the creator's question — there are no
    /// world-facts pinned here that a genuine retry could legitimately differ in.
    /// </remarks>
    private static string SerializeInputs(Guid contextId, IReadOnlyList<string> channelKeys) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(2, StringComparer.Ordinal)
        {
            [ChannelPostsInputs.CreativeContextId] = contextId.ToString("D"),
            [ChannelPostsInputs.ChannelKeys] = string.Join(PhotographyConceptInputs.ListSeparator, channelKeys),
        });

    private static IReadOnlyDictionary<string, string>? DeserializeInputs(string? taskInputsJson) =>
        taskInputsJson is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(taskInputsJson);

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(
            OperationResult<AiProposalStatusServiceModel>.Failure(
                new OperationError(code, message, new Dictionary<string, string[]>())),
            Replayed: false);
}
