using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiPhotographyConceptRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestPhotographyConceptViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting IMG-001's photography concepts: that the task is enabled, that a named recipe
/// is one this workspace has, and which scope the stored row records.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The scope follows the request rather than the task, which no other capability's does.</strong> A
/// request that pins a recipe reads one and changes nothing, which is exactly
/// <see cref="AiOperationScope.Advisory"/>; a request with no recipe names none at all, which is
/// <see cref="AiOperationScope.NotApplicable"/>. Both permit nothing under <c>AiPolicy.AllowedTargets</c>, so
/// the distinction costs no safety and buys an honest row: a later audit can tell a shoot planned against a
/// recipe from one planned from an idea without re-reading the inputs.
/// </para>
/// <para>
/// <strong>A pinned recipe is resolved here, before the operation is written.</strong> The worker would
/// otherwise fail the operation minutes later for a recipe the creator could have been told about
/// immediately — and resolving it through the recipe module's facade is what makes another workspace's recipe
/// indistinguishable from one that does not exist (tenancy.md).
/// </para>
/// </remarks>
internal sealed class AiPhotographyConceptRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IRecipeFacade recipes,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiPhotographyConceptRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestPhotographyConceptViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.PhotographyConcept))
        {
            return Refuse(
                AiPhotographyConceptRequestErrors.TaskNotEnabled,
                "Photography concepts are not enabled for this workspace's deployment.");
        }

        // Free, and a question about the request rather than the account, so it comes before the quota gate.
        if (!await PinResolvesAsync(model, cancellationToken))
        {
            return Refuse(
                AiPhotographyConceptRequestErrors.RecipeNotFound,
                "This workspace has no recipe with that id, or that version is not one of its versions.");
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.PhotographyConcept, cancellationToken) is { } spent)
        {
            return spent;
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.PhotographyConcept,

                // Fixed server-side either way, never taken from the request.
                Scope = model.RecipeId is null
                    ? AiOperationScope.NotApplicable
                    : AiOperationScope.Advisory,
                Status = AiOperationStatus.Requested,
                RecipeId = model.RecipeId,
                RecipeVersionId = model.RecipeVersionId,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = SerializeInputs(model),
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);

        if (requested.Outcome is AiOperationRequestOutcome.KeyReusedForDifferentRequest)
        {
            return Refuse(
                IdempotencyPolicy.KeyReusedCode,
                "That idempotency key was already used for a different request.");
        }

        return new IdempotentOutcome<AiProposalStatusServiceModel>(
            OperationResult<AiProposalStatusServiceModel>.Success(
                AiOperationDescription.Describe(requested.Operation!, null)),
            Replayed: requested.Outcome is AiOperationRequestOutcome.Replayed);
    }

    public async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // One absence for three conditions: no such request, another workspace's, and one that ran a
        // different task. None discloses that it exists.
        if (operation is null || operation.Operation.TaskType != AiTaskType.PhotographyConcept)
        {
            return Failure(
                AiPhotographyConceptRequestErrors.RequestNotFound,
                "That photography concept request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// Whether the recipe and version the request names are this workspace's, or it names none.
    /// </summary>
    /// <remarks>
    /// The snapshot read resolves both at once and refuses a version belonging to some other recipe, which is
    /// the same reasoning <c>PromptRecordBusiness.UnresolvedPinAsync</c> records for a prompt's pins.
    /// </remarks>
    private async Task<bool> PinResolvesAsync(
        RequestPhotographyConceptViewModel model, CancellationToken cancellationToken)
    {
        if (model.RecipeId is not { } recipeId)
        {
            return true;
        }

        return model.RecipeVersionId is { } versionId
            ? (await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken)).Succeeded
            : (await recipes.GetDetailAsync(recipeId, cancellationToken)).Succeeded;
    }

    /// <summary>
    /// The declared fields as JSON, in the shape <see cref="PhotographyConceptAiTaskHandler"/> reads back.
    /// </summary>
    /// <remarks>
    /// Blank values and empty lists are omitted, so an absent key and a blank one mean the same "not
    /// specified". The recipe pin is not here: it lives on the operation's own columns, where the worker
    /// resolves it against the workspace again rather than trusting a payload (tenancy.md).
    /// </remarks>
    private static string SerializeInputs(RequestPhotographyConceptViewModel model)
    {
        var values = new Dictionary<string, string>(4, StringComparer.Ordinal);

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value.Trim();
            }
        }

        Add(PhotographyConceptInputs.ChannelKey, model.ChannelKey);
        Add(PhotographyConceptInputs.CreatorConcept, model.CreatorConcept);
        Add(PhotographyConceptInputs.DishName, model.DishName);
        Add(PhotographyConceptInputs.SceneOverrides, Join(model.SceneOverrides));
        Add(PhotographyConceptInputs.StyleOverrides, Join(model.StyleOverrides));

        return JsonSerializer.Serialize(values);
    }

    /// <summary>
    /// One override list as a single input value, one entry per line.
    /// </summary>
    /// <remarks>
    /// A newline <em>inside</em> an override is flattened to a space before joining, because the separator is
    /// a newline: without this, a creator who pasted a two-line note would silently get two overrides. The
    /// separator is a newline rather than a semicolon or a pipe precisely because those are characters a
    /// creator types, and this is what makes that choice actually hold.
    /// </remarks>
    private static string? Join(IReadOnlyList<string>? values) =>
        values is null or { Count: 0 }
            ? null
            : string.Join(
                PhotographyConceptInputs.ListSeparator,
                values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.ReplaceLineEndings(" ").Trim()));

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
