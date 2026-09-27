using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiConceptRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestRecipeConceptsViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting AIREC-001's recipe concepts: that the task is enabled, and what a creator is
/// shown about a request. Unlike <see cref="AiProposalBusiness"/>, this never reaches into the recipe module —
/// a concept request names no recipe, so there is nothing there to check.
/// </summary>
internal sealed class AiConceptRequestBusiness(
    IAiOperationDataLayer operations,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiConceptRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestRecipeConceptsViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.RecipeConcepts))
        {
            return Refuse(
                AiConceptRequestErrors.TaskNotEnabled,
                "Recipe concept generation is not enabled for this workspace's deployment.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.RecipeConcepts,
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                RecipeId = null,
                RecipeVersionId = null,
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
            OperationResult<AiProposalStatusServiceModel>.Success(Describe(requested.Operation!, null)),
            Replayed: requested.Outcome is AiOperationRequestOutcome.Replayed);
    }

    public async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // Both conditions report the same absence: a request in another workspace is filtered away by the
        // query filter before this ever sees it, and one belonging to some other task type is not this
        // route's resource. Neither discloses that it exists.
        if (operation is null || operation.Operation.TaskType != AiTaskType.RecipeConcepts)
        {
            return Failure(AiConceptRequestErrors.RequestNotFound, "That recipe concept request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// The declared fields as JSON, in the shape <c>RecipeConceptsAiTaskHandler</c> reads back — blank fields
    /// omitted, since an absent key and a blank one mean the same "not specified" to that handler.
    /// </summary>
    private static string SerializeInputs(RequestRecipeConceptsViewModel model)
    {
        var values = new Dictionary<string, string>(11, StringComparer.Ordinal);

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value;
            }
        }

        Add("audience", model.Audience);
        Add("course", model.Course);
        Add("cuisine", model.Cuisine);
        Add("dietaryGoals", model.DietaryGoals);
        Add("availableIngredients", model.AvailableIngredients);
        Add("exclusions", model.Exclusions);
        Add("equipment", model.Equipment);
        Add("skill", model.Skill);
        Add("season", model.Season);
        Add("timeBudget", model.TimeBudget);
        Add("creatorStyle", model.CreatorStyle);

        return JsonSerializer.Serialize(values);
    }

    private static AiProposalStatusServiceModel Describe(AiOperation operation, AiProposal? proposal) =>
        new(
            operation.Id,
            operation.Status,
            operation.TaskType,
            operation.Scope,
            operation.RecipeVersionId,
            operation.RequestedAt,
            operation.StatusChangedAt,
            operation.FailureCategory,
            proposal is null ? null : Describe(proposal));

    private static AiProposalDetailServiceModel Describe(AiProposal proposal) =>
        new(
            proposal.Id,
            proposal.OutputSchemaVersion,
            proposal.PromptTemplateId,
            proposal.PromptTemplateVersion,
            proposal.PromptTemplateBodyChecksum,
            proposal.ProviderName,
            proposal.ModelName,
            proposal.CreatedAt,
            [.. proposal.Changes
                .OrderBy(change => change.SortOrder)
                .Select(change => new AiProposedChangeServiceModel(
                    change.Id,
                    change.ChangeKind,
                    change.TargetKind,
                    change.TargetId,
                    change.FieldName,
                    change.BeforeValue,
                    change.AfterValue,
                    change.ProposedPosition,
                    change.Disposition))],
            [.. proposal.Warnings
                .OrderBy(warning => warning.SortOrder)
                .Select(warning => new AiProposalWarningServiceModel(
                    warning.Kind, warning.Message, warning.AiStructuredChangeId))]);

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
