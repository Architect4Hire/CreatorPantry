using System.Diagnostics;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiProposalBusiness
{
    /// <remarks>
    /// Returns the replay flag rather than leaving the caller to infer one. Whether this request created the
    /// operation or found an identical earlier one is something the data layer <em>knows</em>, and deriving it
    /// from timestamps upstream would be guessing at an answer already in hand.
    /// </remarks>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestAiProposalViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid recipeId,
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what a creator decided about a proposal, applying whatever they accepted.
    /// </summary>
    /// <remarks>
    /// Owns the selection rules, the status the decision produces, and the translation of accepted changes into
    /// the recipe module's vocabulary. The transaction that makes it atomic belongs to the DataLayer.
    /// </remarks>
    Task<OperationResult<AiProposalDispositionServiceModel>> DispositionAsync(
        string actorUserId,
        Guid recipeId,
        Guid requestId,
        AiProposalDispositionViewModel model,
        CancellationToken cancellationToken);
}

/// <summary>
/// The rules around requesting a proposal: which tasks may run, that the source is this recipe's, and what a
/// creator is shown about one.
/// </summary>
/// <remarks>
/// Calls the DataLayer and the recipe module's <em>facade</em>, which is the only way a module may reach
/// another. It never touches a provider: requesting a proposal queues work, and the model is called by the
/// worker long after this returns.
/// </remarks>
internal sealed class AiProposalBusiness(
    IAiOperationDataLayer operations,
    IRecipeFacade recipes,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiProposalBusiness
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid recipeId,
        RequestAiProposalViewModel model,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var task = AiTaskCatalog.Resolve(model.Task);

        if (task is null)
        {
            return Refuse(AiProposalErrors.TaskUnknown, "That task does not exist.");
        }

        // Known but not switched on. A different answer from "no such task", because the deployment could
        // enable it and the caller has not asked for something meaningless.
        if (!AiTaskCatalog.IsEnabled(tasks, model.Task!))
        {
            return Refuse(
                AiProposalErrors.TaskNotEnabled,
                "That task is not enabled for this workspace's deployment.");
        }

        // Through the recipe module's facade, never its repositories. This is also the existence check: a
        // recipe in another workspace is invisible here, so it reports missing rather than forbidden.
        var recipe = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!recipe.Succeeded)
        {
            return Refuse(AiProposalErrors.RecipeNotFound, "That recipe does not exist.");
        }

        // The pinned source must be a version of this recipe. Without the check the composite foreign key
        // would still refuse a foreign version, but as a database error rather than an answer.
        if (recipe.Value!.CurrentVersion?.Id != model.SourceVersionId)
        {
            return Refuse(
                AiProposalErrors.SourceVersionInvalid,
                "That version is not the current version of this recipe.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = task.Value,
                Scope = model.Scope,
                Status = AiOperationStatus.Requested,
                RecipeId = recipeId,
                RecipeVersionId = model.SourceVersionId,
                IdempotencyKey = idempotencyKey,
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
        Guid recipeId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var operation = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // Both conditions report the same absence: an operation in another workspace is filtered away, and one
        // belonging to a different recipe is not this route's resource. Neither discloses that it exists.
        if (operation is null || operation.Operation.RecipeId != recipeId)
        {
            return Failure(AiProposalErrors.RequestNotFound, "That proposal request does not exist.");
        }

        return OperationResult<AiProposalStatusServiceModel>.Success(
            Describe(operation.Operation, operation.Proposal));
    }

    public async Task<OperationResult<AiProposalDispositionServiceModel>> DispositionAsync(
        string actorUserId,
        Guid recipeId,
        Guid requestId,
        AiProposalDispositionViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var loaded = await operations.GetForDispositionAsync(requestId, cancellationToken);

        // The same absence for an operation in another workspace, one belonging to a different recipe, and one
        // that does not exist. None of the three discloses anything about the others.
        if (loaded is null || loaded.Operation.RecipeId != recipeId)
        {
            return Failure<AiProposalDispositionServiceModel>(
                AiProposalErrors.RequestNotFound, "That proposal request does not exist.");
        }

        var proposal = loaded.Proposal;

        if (proposal is null)
        {
            return Failure<AiProposalDispositionServiceModel>(
                AiProposalErrors.ProposalNotFound,
                "That request has not produced a proposal to decide about.");
        }

        if (loaded.Operation.Status is not AiOperationStatus.Proposed
            && !AiOperationTransitionPolicy.IsTerminal(loaded.Operation.Status))
        {
            // Still queued or running. Its own answer, because waiting is the remedy — unlike a terminal
            // operation, where nothing the creator does will make it decidable again.
            return Failure<AiProposalDispositionServiceModel>(
                AiProposalErrors.ProposalDecided,
                "That proposal is not ready to be decided about yet.");
        }

        IReadOnlyList<Guid> accepted = model.Decision is AiDispositionDecision.Reject
            ? []
            : model.AcceptedChangeIds ?? [];

        if (Select(proposal, model.Decision, accepted) is { } selectionError)
        {
            return selectionError;
        }

        var status = accepted.Count == 0
            ? AiOperationStatus.Rejected
            : accepted.Count == proposal.Changes.Count
                ? AiOperationStatus.Accepted
                : AiOperationStatus.PartiallyAccepted;

        // Asked rather than assumed. The policy is the single source of which moves are legal, and a status this
        // seam computed that the policy would refuse is a bug worth failing loudly on rather than persisting.
        if (!AiOperationTransitionPolicy.IsAllowed(AiOperationStatus.Proposed, status))
        {
            throw new InvalidOperationException(
                $"A disposition computed a status an AI operation cannot move to: {status}.");
        }

        var acceptedChanges = proposal.Changes.Where(change => accepted.Contains(change.Id)).ToList();

        var instruction = new AiDispositionInstruction(
            accepted.ToHashSet(),
            status,
            workspace.MembershipId,
            new AuditEntry(
                actorUserId,
                AiOperationTransitionPolicy.AuditAction(AiOperationStatus.Proposed, status),
                AiAuditResources.Proposal,
                proposal.Id.ToString("D"),
                CorrelationId(),

                // Counts and a decision, never content. A summary quoting what was accepted would put generated
                // recipe text into the audit log, which AuditLog's own remarks forbid. The safety-caution count
                // is a count for the same reason, and it is here because ai.md asks for uncertainty to be
                // surfaced: without it the record cannot say afterwards whether what the creator took carried a
                // caution, and "accepted two changes" reads identically either way.
                $"{model.Decision} — {accepted.Count} of {proposal.Changes.Count} changes accepted, "
                    + $"{CautionedCount(proposal, accepted)} of them carrying a safety caution.",
                BeforeReference: AiOperationStatus.Proposed.ToString(),
                AfterReference: status.ToString()),
            Feedback(proposal.Id, model));

        var write = await operations.DispositionAsync(
            requestId,
            instruction,

            // Closes over the translated changes so that the recipe facade is called from here — Business is the
            // only layer permitted to reach another module — while the transaction around it belongs to the
            // DataLayer, which is the layer that owns transaction boundaries.
            token => ApplyAsync(recipeId, proposal, acceptedChanges, token),
            cancellationToken);

        return write.Outcome switch
        {
            AiDispositionOutcome.Applied or AiDispositionOutcome.Replayed =>
                OperationResult<AiProposalDispositionServiceModel>.Success(new AiProposalDispositionServiceModel(
                    requestId,
                    status,
                    accepted.Count,
                    proposal.Changes.Count - accepted.Count,
                    write.RecipeVersionNumber,
                    clock.UtcNow)),

            AiDispositionOutcome.NotFound => Failure<AiProposalDispositionServiceModel>(
                AiProposalErrors.RequestNotFound, "That proposal request does not exist."),

            // The recipe domain's own refusal, passed through unchanged. A stale source arrives here as a recipe
            // conflict, and that is the right answer to give: the remedy is a recipe remedy — re-read it and ask
            // again — so translating the code into an AI one would only obscure where to look.
            AiDispositionOutcome.ApplyRefused =>
                OperationResult<AiProposalDispositionServiceModel>.Failure(write.Error!),

            _ => Failure<AiProposalDispositionServiceModel>(
                AiProposalErrors.ProposalDecided,
                "That proposal has already been decided, and a decision cannot be changed."),
        };
    }

    /// <summary>
    /// How many of the accepted changes had a safety caution attached to them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A number, never the caution's text. <c>AiWarningKind.SafetyCaution</c> covers preservation, temperature,
    /// allergens and dietary suitability — the claims recipes.md refuses to let anything present as guaranteed —
    /// and an audit trail that could not distinguish "took two suggestions" from "took two suggestions, one of
    /// which came with a caution" would be unable to answer the only question worth asking about this write later.
    /// </para>
    /// <para>
    /// Cautions attached to the answer as a whole are not counted. They were shown to the creator at review time,
    /// but they say nothing about which changes were taken, and counting them here would inflate the number in a
    /// way nobody reading the row could unpick.
    /// </para>
    /// </remarks>
    private static int CautionedCount(AiProposal proposal, IReadOnlyList<Guid> accepted) =>
        proposal.Warnings
            .Where(warning => warning.Kind is AiWarningKind.SafetyCaution)
            .Select(warning => warning.AiStructuredChangeId)
            .Where(changeId => changeId is { } id && accepted.Contains(id))
            .Distinct()
            .Count();

    /// <summary>
    /// Whether the confirmed selection is one this proposal can honour.
    /// </summary>
    /// <remarks>
    /// Returns a refusal rather than throwing, and refuses before anything is written: an invalid selection must
    /// not apply the part of itself that was valid. The shape checks — non-empty, no duplicates, present for an
    /// accept — belong to the validator; what is checked here needs the proposal.
    /// </remarks>
    private static OperationResult<AiProposalDispositionServiceModel>? Select(
        AiProposal proposal,
        AiDispositionDecision decision,
        IReadOnlyList<Guid> accepted)
    {
        var known = proposal.Changes.Select(change => change.Id).ToHashSet();

        if (accepted.Any(id => !known.Contains(id)))
        {
            return Failure<AiProposalDispositionServiceModel>(
                AiProposalErrors.SelectionInvalid,
                "One of the accepted changes does not belong to this proposal.");
        }

        // What makes AcceptAll a confirmation rather than a flag. A client that names fewer changes than the
        // proposal holds is looking at something other than this proposal, and accepting everything on its word
        // would apply changes nobody reviewed.
        if (decision is AiDispositionDecision.AcceptAll && accepted.Count != proposal.Changes.Count)
        {
            return Failure<AiProposalDispositionServiceModel>(
                AiProposalErrors.SelectionInvalid,
                $"Accepting everything means naming all {proposal.Changes.Count} changes. Name them, or accept "
                    + "a selection instead.");
        }

        return null;
    }

    /// <summary>
    /// Applies the accepted changes through the recipe module's facade and reports the version they wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The facade, never anything below it. Every rule an ordinary edit obeys therefore applies — the role check,
    /// the archived-recipe refusal, the recipe's own invariants, the staleness check against the pinned version.
    /// A model's output cannot reach a recipe by a shorter route.
    /// </para>
    /// <para>
    /// A change the recipe module has no field for is refused there, and the refusal is returned rather than
    /// swallowed. Nothing is offered that cannot be applied, though — <c>AiChangeApplicability</c> is what keeps
    /// this from being the place a creator first finds out.
    /// </para>
    /// </remarks>
    private async Task<OperationResult<int?>> ApplyAsync(
        Guid recipeId,
        AiProposal proposal,
        IReadOnlyList<AiStructuredChange> accepted,
        CancellationToken cancellationToken)
    {
        if (proposal.SourceRecipeVersionId is not { } sourceVersionId)
        {
            // A proposal that pinned no version cannot be accepted onto a recipe: there is nothing to check
            // staleness against, and AIREC-GR-002 turns on that check.
            return OperationResult<int?>.Failure(new OperationError(
                AiProposalErrors.SourceVersionInvalid,
                "That proposal names no source version, so its changes cannot be applied to a recipe.",
                new Dictionary<string, string[]>()));
        }

        var translated = new List<ProposedRecipeChange>(accepted.Count);

        foreach (var change in accepted)
        {
            // A refusal rather than a throw. AiChangeApplicability stops such a change being stored, but a row
            // written before that gate existed — or by a future writer that forgets it — must produce an answer
            // and not a 500 inside the transaction. The stored row is not the creator's fault either way.
            if (Translate(change) is not { } expressible)
            {
                return OperationResult<int?>.Failure(new OperationError(
                    AiProposalErrors.SelectionInvalid,
                    $"One accepted change is a {change.ChangeKind} on a {change.TargetKind}, which cannot be "
                        + "applied to a recipe.",
                    new Dictionary<string, string[]>()));
            }

            translated.Add(expressible);
        }

        var applied = await recipes.ApplyProposedChangesAsync(
            recipeId,
            sourceVersionId,
            proposal.Id,
            translated,
            cancellationToken);

        if (!applied.Succeeded)
        {
            return OperationResult<int?>.Failure(applied.Error!);
        }

        // Compared against the pinned version rather than reported blindly. Accepted changes that all match the
        // recipe's current values write no version at all — the same no-op rule a creator's own edit follows —
        // and reporting the version that was already there as one this call wrote would be a small lie.
        var current = applied.Value!.CurrentVersion;

        return OperationResult<int?>.Success(
            current is null || current.Id == sourceVersionId ? null : current.VersionNumber);
    }

    /// <summary>
    /// One stored change in the recipe module's vocabulary, or <c>null</c> when that vocabulary cannot express it.
    /// </summary>
    /// <remarks>
    /// A translation rather than a shared type, because neither module may name the other's internals. The two
    /// vocabularies are deliberately not the same size: this module can describe a change to an ingredient and
    /// the recipe seam cannot apply one, so the narrower enum is what the boundary speaks. An inexpressible
    /// change answers <c>null</c> rather than throwing — the caller turns it into a refusal, because a stored row
    /// the applicability gate should have prevented is a defect to report, not an exception to raise inside a
    /// transaction.
    /// </remarks>
    private static ProposedRecipeChange? Translate(AiStructuredChange change)
    {
        var kind = change.ChangeKind switch
        {
            AiChangeKind.Set => ProposedRecipeChangeKind.Set,
            AiChangeKind.Add => ProposedRecipeChangeKind.Add,
            AiChangeKind.Remove => ProposedRecipeChangeKind.Remove,
            AiChangeKind.Move => ProposedRecipeChangeKind.Move,
            _ => (ProposedRecipeChangeKind?)null,
        };

        var target = change.TargetKind switch
        {
            AiChangeTargetKind.Recipe => ProposedRecipeTarget.Recipe,
            AiChangeTargetKind.InstructionGroup => ProposedRecipeTarget.InstructionGroup,
            AiChangeTargetKind.InstructionStep => ProposedRecipeTarget.InstructionStep,
            AiChangeTargetKind.Tag => ProposedRecipeTarget.Tag,
            _ => (ProposedRecipeTarget?)null,
        };

        return kind is { } expressibleKind && target is { } expressibleTarget
            ? new ProposedRecipeChange(
                expressibleKind,
                expressibleTarget,
                change.TargetId,
                change.FieldName,
                change.AfterValue,
                change.ProposedPosition)
            : null;
    }

    /// <summary>
    /// The feedback row a disposition leaves, or <c>null</c> when the creator said nothing.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty row, because a check constraint refuses feedback that says nothing — without
    /// this, every disposition would accumulate a row recording only that someone opened the panel.
    /// </remarks>
    private AiProposalFeedback? Feedback(Guid proposalId, AiProposalDispositionViewModel model)
    {
        var comment = model.Comment?.Trim();

        if (model.WasHelpful is null && string.IsNullOrEmpty(comment))
        {
            return null;
        }

        return new AiProposalFeedback
        {
            WorkspaceId = workspace.WorkspaceId,
            AiProposalId = proposalId,
            MembershipId = workspace.MembershipId,
            WasHelpful = model.WasHelpful,
            Comment = string.IsNullOrEmpty(comment) ? null : comment,
            CreatedAt = clock.UtcNow,
        };
    }

    /// <summary>
    /// The ambient request's trace id, as a <see cref="Guid"/>, so an audit row can be joined to the traces of
    /// the request that wrote it.
    /// </summary>
    /// <remarks>
    /// Read from <see cref="Activity"/> rather than threaded through four layers as a parameter, for the reason
    /// the recipe module's copy of this records. A W3C trace id is sixteen bytes, the same width as a
    /// <see cref="Guid"/>, so the two are one value in two spellings and an operator can paste one into the
    /// other. A new id when there is no activity, so an entry written outside a traced request is at least
    /// correlatable with itself.
    /// </remarks>
    private static Guid CorrelationId()
    {
        var traceId = Activity.Current?.TraceId;

        return traceId is { } id && id != default
            ? Guid.ParseExact(id.ToHexString(), "N")
            : Guid.NewGuid();
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

    private static OperationResult<AiProposalStatusServiceModel> Failure(string code, string message) =>
        Failure<AiProposalStatusServiceModel>(code, message);

    /// <summary>
    /// One refusal shape for every reply this seam gives, whatever it was answering.
    /// </summary>
    /// <remarks>
    /// Generic over the reply so a status read and a disposition refuse in the same words under the same code.
    /// Two spellings of "that request does not exist" would eventually become two ways to tell apart the cases
    /// tenancy.md requires to be indistinguishable.
    /// </remarks>
    private static OperationResult<T> Failure<T>(string code, string message) =>
        OperationResult<T>.Failure(new OperationError(code, message, new Dictionary<string, string[]>()));

    /// <summary>A refusal never replays: nothing was created, so there is nothing to return again.</summary>
    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(string code, string message) =>
        new(Failure(code, message), Replayed: false);
}
