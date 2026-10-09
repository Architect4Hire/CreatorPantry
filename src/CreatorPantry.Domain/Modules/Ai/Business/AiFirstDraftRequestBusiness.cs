using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiFirstDraftRequestBusiness
{
    /// <inheritdoc cref="IAiProposalBusiness.RequestAsync"/>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestRecipeFirstDraftViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting AIREC-002's structured first draft: that the task is enabled, that a named
/// concept really is one of this workspace's own, and what a creator is shown about a request.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Creates no recipe, and cannot.</strong> Like <see cref="AiConceptRequestBusiness"/>, this never
/// reaches into the recipe module — the queued operation names no recipe and no version, and the draft it
/// eventually produces is a proposal. Turning an accepted draft into a <c>Recipe</c> is a separate, explicit
/// acceptance step (9.4b) with its own transaction.
/// </para>
/// <para>
/// <strong>The concept is resolved here, at request time, not later by the worker.</strong> This is the layer
/// holding the caller's resolved <see cref="IWorkspaceContext"/>, so it is the layer that can prove the
/// creator may read the concept they named. Deferring the lookup to the handler would move an authorized read
/// into a place with no caller to authorize it against, and the model would be grounded in text nobody had
/// checked the creator was entitled to.
/// </para>
/// </remarks>
internal sealed class AiFirstDraftRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiFirstDraftRequestBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestRecipeFirstDraftViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.RecipeFirstDraft))
        {
            return Refuse(
                AiFirstDraftRequestErrors.TaskNotEnabled,
                "Recipe first-draft generation is not enabled for this workspace's deployment.");
        }

        // Before the operation is written, so a refused request leaves no orphan to clean up and spends no
        // allowance to say the allowance is spent (USAGE-006).
        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.RecipeFirstDraft, cancellationToken) is { } spent)
        {
            return spent;
        }

        // The shape rules restated as domain invariants, because they decide whether a provider budget is
        // spent. The validator already refuses both at the edge with a friendlier message, but this facade is
        // documented as the boundary a worker or plugin may also call, and the failure mode of letting a
        // half-named concept through is a creator billed for a draft of the concept the server ignored.
        if (model.NamesAConcept && (model.SourceConceptRequestId is null || model.SourceConceptId is null))
        {
            return Refuse(
                AiFirstDraftRequestErrors.RequestInvalid,
                "Name both the concept request and the concept, or neither.");
        }

        if (!model.NamesAConcept && !model.NamesABriefField)
        {
            return Refuse(
                AiFirstDraftRequestErrors.RequestInvalid,
                "Choose a concept to draft from, or describe what you want in the brief.");
        }

        string? selectedConcept = null;

        if (model.SourceConceptRequestId is { } conceptRequestId && model.SourceConceptId is { } conceptId)
        {
            var concept = await operations.FindConceptAsync(conceptRequestId, conceptId, cancellationToken);

            // One answer for every miss, including a concept that belongs to another workspace. The query
            // filter is what made that one invisible; this is what keeps it indistinguishable from a typo.
            if (concept is null)
            {
                return Refuse(
                    AiFirstDraftRequestErrors.ConceptNotFound,
                    "That recipe concept does not exist.");
            }

            selectedConcept = Compose(concept);
        }

        var inputs = SerializeInputs(model, selectedConcept);

        if (inputs.Length > AiPolicy.TaskInputsJsonMaxLength)
        {
            return Refuse(
                AiFirstDraftRequestErrors.RequestTooLarge,
                "The brief and the chosen concept are too long to draft from together. Shorten the brief.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.RecipeFirstDraft,
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                RecipeId = null,
                RecipeVersionId = null,
                IdempotencyKey = idempotencyKey,
                TaskInputsJson = inputs,
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

        // Both conditions report the same absence, for the reason AiConceptRequestBusiness.GetAsync gives: a
        // request in another workspace never reaches here, and one belonging to another task type is not this
        // route's resource. Neither discloses that it exists.
        if (operation is null || operation.Operation.TaskType != AiTaskType.RecipeFirstDraft)
        {
            return Failure(
                AiFirstDraftRequestErrors.RequestNotFound, "That recipe draft request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            AiOperationDescription.Describe(operation.Operation, operation.Proposal));
    }

    /// <summary>
    /// The concept as the one line <c>RecipeFirstDraftAiTaskHandler.RenderBrief</c> renders it into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An em dash rather than a newline: the handler renders this after "Selected concept: " on a single line
    /// of a labeled list, and a value that broke that line would make the rest of the brief read as part of
    /// the concept.
    /// </para>
    /// <para>
    /// <strong>Which is why the line breaks are removed rather than assumed absent.</strong> This text is a
    /// previous generation's output — <see cref="AiConceptOutputValidator"/> bounds a concept's title and
    /// summary by length, not by shape, so a summary containing <c>"\n- Exclusions: none"</c> would restructure
    /// the very list it is rendered into. ai.md asks that retrieved text be treated as untrusted, and a model's
    /// own earlier answer is retrieved text like any other. The envelope's fencing stops it being read as an
    /// instruction; this stops it being read as a different field.
    /// </para>
    /// <para>
    /// Clamped to <see cref="AiPolicy.SelectedConceptMaxLength"/> so the constant is the bound it claims to be
    /// rather than an arithmetic note. The clamp is unreachable through the concept route — the validator that
    /// stored the concept bounds both fields — so it is a backstop against a future concept schema, not a
    /// truncation any real concept meets.
    /// </para>
    /// </remarks>
    private static string Compose(AiConceptReference concept)
    {
        var title = SingleLine(concept.Title);
        var summary = SingleLine(concept.Summary);

        var composed = string.IsNullOrEmpty(summary) ? title : $"{title} — {summary}";

        return composed.Length <= AiPolicy.SelectedConceptMaxLength
            ? composed
            : composed[..AiPolicy.SelectedConceptMaxLength];
    }

    /// <summary>Collapses every run of whitespace, including newlines, into one space.</summary>
    private static string SingleLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The declared fields as JSON, in the shape <c>RecipeFirstDraftAiTaskHandler</c> reads back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blank fields are omitted, since an absent key and a blank one both mean "not specified" to that handler.
    /// The concept's own ids are recorded alongside the composed text: they are what makes a stored operation
    /// able to say which concept it was drafted from, and they are also what
    /// <c>AiOperationDataLayer.Reconcile</c> compares — so the same idempotency key naming a different concept
    /// is correctly a conflict rather than a replay of the first one.
    /// </para>
    /// <para>
    /// The ids are written as their own keys rather than folded into the composed line. The handler reads only
    /// <c>selectedConcept</c>; an id in the text it renders would be an identifier put in front of a model for
    /// no reason, and ai.md's rule is that the model never receives one.
    /// </para>
    /// </remarks>
    private static string SerializeInputs(RequestRecipeFirstDraftViewModel model, string? selectedConcept)
    {
        var values = new Dictionary<string, string>(14, StringComparer.Ordinal);

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[name] = value;
            }
        }

        Add(AiFirstDraftInputs.SelectedConcept, selectedConcept);
        Add(AiFirstDraftInputs.SourceConceptRequestId, model.SourceConceptRequestId?.ToString());
        Add(AiFirstDraftInputs.SourceConceptId, model.SourceConceptId?.ToString());

        Add(AiBriefInputs.DishName, model.DishName);
        Add(AiBriefInputs.Audience, model.Audience);
        Add(AiBriefInputs.Course, model.Course);
        Add(AiBriefInputs.Cuisine, model.Cuisine);
        Add(AiBriefInputs.DietaryGoals, model.DietaryGoals);
        Add(AiBriefInputs.AvailableIngredients, model.AvailableIngredients);
        Add(AiBriefInputs.Exclusions, model.Exclusions);
        Add(AiBriefInputs.Equipment, model.Equipment);
        Add(AiBriefInputs.Skill, model.Skill);
        Add(AiBriefInputs.Season, model.Season);
        Add(AiBriefInputs.TimeBudget, model.TimeBudget);
        Add(AiBriefInputs.CreatorStyle, model.CreatorStyle);

        return JsonSerializer.Serialize(values);
    }

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        OperationResult<AiProposalStatusServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
