using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiImagePromptRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestImagePromptViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting IMG-002's composed prompt: that the task is enabled, that the concept and shot
/// are ones this workspace was shown, and that a named recipe and brief are its own.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything is resolved before the operation is written</strong>, so a creator learns immediately
/// rather than finding a failed operation minutes later — and each resolution goes through the owning
/// module's facade, which is what makes a neighbour's concept, recipe or brief indistinguishable from one that
/// does not exist (tenancy.md).
/// </para>
/// <para>
/// <strong>The scope follows the recipe pin</strong>, as IMG-001's does and for the same reason.
/// </para>
/// </remarks>
internal sealed class AiImagePromptRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IRecipeFacade recipes,
    IBrandSourceDocumentFacade documents,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiImagePromptRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestImagePromptViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.ImagePrompt))
        {
            return Refuse(
                AiImagePromptRequestErrors.TaskNotEnabled,
                "Image prompt composition is not enabled for this workspace's deployment.");
        }

        if (!await ConceptResolvesAsync(model, cancellationToken))
        {
            return Refuse(
                AiImagePromptRequestErrors.ConceptNotFound,
                "That concept and shot are not ones this workspace can compose from.");
        }

        if (!await PinResolvesAsync(model, cancellationToken))
        {
            return Refuse(
                AiImagePromptRequestErrors.RecipeNotFound,
                "This workspace has no recipe with that id, or that version is not one of its versions.");
        }

        var brief = await ResolveBriefAsync(model, cancellationToken);

        if (!brief.Ok)
        {
            return Refuse(
                AiImagePromptRequestErrors.BriefNotFound,
                "This workspace has no document with that id.");
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.ImagePrompt, cancellationToken) is { } spent)
        {
            return spent;
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.ImagePrompt,
                Scope = model.RecipeId is null
                    ? AiOperationScope.NotApplicable
                    : AiOperationScope.Advisory,
                Status = AiOperationStatus.Requested,
                RecipeId = model.RecipeId,
                RecipeVersionId = model.RecipeVersionId,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = SerializeInputs(model, brief.VersionNumber),
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

        if (operation is null || operation.Operation.TaskType != AiTaskType.ImagePrompt)
        {
            return Failure(
                AiImagePromptRequestErrors.RequestNotFound, "That image prompt request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// Whether the concept and shot are ones this workspace was shown.
    /// </summary>
    /// <remarks>
    /// The same read the handler does, asked early so the refusal is immediate. It is asked twice rather than
    /// trusted once: the worker claims an operation minutes later and re-resolves everything, because a
    /// concept could be a proposal that has since expired.
    /// </remarks>
    private async Task<bool> ConceptResolvesAsync(
        RequestImagePromptViewModel model, CancellationToken cancellationToken)
    {
        var found = await operations.GetWithProposalAsync(model.ConceptRequestId, cancellationToken);

        if (found?.Proposal is null || found.Operation.TaskType is not AiTaskType.PhotographyConcept)
        {
            return false;
        }

        return AiImagePromptConcept.From(
            found.Proposal.Changes.Select(change =>
                (change.TargetKind, change.TargetId, change.FieldName, change.AfterValue)),
            model.ConceptId,
            model.ShotKind!.Value) is not null;
    }

    /// <inheritdoc cref="AiPhotographyConceptRequestBusiness"/>
    private async Task<bool> PinResolvesAsync(
        RequestImagePromptViewModel model, CancellationToken cancellationToken)
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
    /// Whether the attached brief is a document of this workspace.
    /// </summary>
    /// <remarks>
    /// Resolved here so an id belonging to a neighbour is refused at the request rather than silently
    /// supplying no text at generation time — the creator asked for their brief to be used, and "it was not"
    /// is a different answer from "that is not your document".
    /// </remarks>
    private async Task<(bool Ok, int? VersionNumber)> ResolveBriefAsync(
        RequestImagePromptViewModel model, CancellationToken cancellationToken)
    {
        if (model.BriefDocumentId is not { } documentId)
        {
            return (true, null);
        }

        var document = await documents.GetAsync(documentId, cancellationToken);

        // The version is captured here, not looked up again in the worker: the brief that reaches the model
        // is the one the creator attached, even if they replace the document in the minutes before the
        // operation is claimed.
        return document.Succeeded && document.Value is not null
            ? (true, document.Value.CurrentVersion.VersionNumber)
            : (false, null);
    }

    private static string SerializeInputs(RequestImagePromptViewModel model, int? briefVersionNumber)
    {
        var values = new Dictionary<string, string>(7, StringComparer.Ordinal)
        {
            [ImagePromptInputs.ConceptRequestId] = model.ConceptRequestId.ToString(),
            [ImagePromptInputs.ConceptId] = model.ConceptId.ToString(),
            [ImagePromptInputs.ShotKind] = model.ShotKind!.Value.ToString(),
        };

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value.Trim();
            }
        }

        Add(ImagePromptInputs.ChannelKey, model.ChannelKey);
        Add(ImagePromptInputs.BriefDocumentId, model.BriefDocumentId?.ToString());
        Add(ImagePromptInputs.BriefVersionNumber, briefVersionNumber?.ToString(CultureInfo.InvariantCulture));
        Add(ImagePromptInputs.SceneOverrides, Join(model.SceneOverrides));
        Add(ImagePromptInputs.StyleOverrides, Join(model.StyleOverrides));

        return JsonSerializer.Serialize(values);
    }

    /// <inheritdoc cref="AiPhotographyConceptRequestBusiness"/>
    private static string? Join(IReadOnlyList<string>? values) =>
        values is null or { Count: 0 }
            ? null
            : string.Join(
                ImagePromptInputs.ListSeparator,
                values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.ReplaceLineEndings(" ").Trim()));

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
