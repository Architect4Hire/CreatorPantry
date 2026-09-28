using System.Diagnostics;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiDraftAcceptanceBusiness
{
    /// <summary>
    /// Records what the creator decided about a first draft, and creates the recipe if they accepted one.
    /// </summary>
    Task<OperationResult<AiDraftAcceptanceServiceModel>> AcceptAsync(
        string actorUserId,
        Guid requestId,
        AiDraftAcceptanceViewModel model,
        CancellationToken cancellationToken);
}

/// <summary>
/// The rules around accepting AIREC-002's structured first draft into a new recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only thing in the system that turns a generated draft into a recipe.</strong> Nothing
/// else writes one from a proposal, and this writes exactly one — inside the transaction
/// <see cref="IAiOperationDataLayer.AcceptDraftAsync"/> owns, through the recipe module's own facade, which is
/// the only route a module may take into another.
/// </para>
/// <para>
/// <strong>It creates no recipe for a rejection, and that is not a special case.</strong> A rejection accepts
/// nothing, so the composer has nothing to compose and the data layer skips the create — one code path, with
/// the decision deciding what happens rather than a branch deciding which decision is real.
/// </para>
/// </remarks>
internal sealed class AiDraftAcceptanceBusiness(
    IAiOperationDataLayer operations,
    IRecipeFacade recipes,
    IWorkspaceContext workspace,
    IClock clock) : IAiDraftAcceptanceBusiness
{
    public async Task<OperationResult<AiDraftAcceptanceServiceModel>> AcceptAsync(
        string actorUserId,
        Guid requestId,
        AiDraftAcceptanceViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Checked here, not only at the controller policy: this boundary is also reachable by a worker or a
        // plugin, which no MVC policy protects. The recipe facade checks it again — it has to, it is a
        // separate module — and this is so the refusal names deciding rather than creating.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Failure(
                AiDraftAcceptanceErrors.AcceptanceForbidden,
                "You do not have permission to accept a draft into a recipe in this workspace.");
        }

        var loaded = await operations.GetForDispositionAsync(requestId, cancellationToken);

        // The same absence for an operation in another workspace, one belonging to a different task, and one
        // that does not exist. None of the three discloses anything about the others (tenancy.md).
        if (loaded is null || loaded.Operation.TaskType != AiTaskType.RecipeFirstDraft)
        {
            return Failure(
                AiFirstDraftRequestErrors.RequestNotFound, "That recipe draft request does not exist.");
        }

        if (loaded.Proposal is not { } proposal)
        {
            return Failure(
                AiDraftAcceptanceErrors.DraftNotFound,
                "That request has not produced a draft to decide about.");
        }

        if (loaded.Operation.Status is not AiOperationStatus.Proposed
            && !AiOperationTransitionPolicy.IsTerminal(loaded.Operation.Status))
        {
            // Still queued or running. Its own answer, because waiting is the remedy — unlike a terminal
            // operation, where nothing the creator does will make it decidable again.
            return Failure(
                AiDraftAcceptanceErrors.DraftDecided, "That draft is not ready to be decided about yet.");
        }

        IReadOnlyList<Guid> accepted = model.Decision is AiDispositionDecision.Reject
            ? []
            : model.AcceptedChangeIds ?? [];

        if (Select(proposal, model.Decision, accepted) is { } selectionError)
        {
            return selectionError;
        }

        var changes = proposal.Changes.OrderBy(change => change.SortOrder).ToList();
        var acceptedIds = accepted.ToHashSet();

        if (ReadEdits(changes, acceptedIds, model.Edits, out var edits) is { } editError)
        {
            return editError;
        }

        // Composed before the transaction opens. It reads only stored rows and the creator's own edits and
        // makes no database call, so nothing it does has to be rolled back.
        var composition = accepted.Count == 0
            ? null
            : AiRecipeDraftComposer.Compose(changes, acceptedIds, edits);

        // Refused here as well as by the create validator, which is where the rule actually lives. Checking it
        // first only makes the refusal cheap — the guarantee is that nothing is written either way, and the
        // validator inside the transaction is what provides it.
        if (composition is not null && string.IsNullOrWhiteSpace(composition.Draft.Title))
        {
            return Failure(
                AiDraftAcceptanceErrors.SelectionInvalid,
                "A recipe needs a title. Accept the draft's title, or write one of your own, and try again.");
        }

        var status = accepted.Count == 0
            ? AiOperationStatus.Rejected
            : accepted.Count == changes.Count
                ? AiOperationStatus.Accepted
                : AiOperationStatus.PartiallyAccepted;

        // Asked rather than assumed, the same way the disposition seam asks: the policy is the single source of
        // which moves are legal, and a status computed here that it would refuse is a bug worth failing on.
        if (!AiOperationTransitionPolicy.IsAllowed(AiOperationStatus.Proposed, status))
        {
            throw new InvalidOperationException(
                $"An acceptance computed a status an AI operation cannot move to: {status}.");
        }

        var instruction = new AiDispositionInstruction(
            acceptedIds,
            status,
            workspace.MembershipId,
            new AuditEntry(
                actorUserId,
                AiOperationTransitionPolicy.AuditAction(AiOperationStatus.Proposed, status),
                AiAuditResources.Proposal,
                proposal.Id.ToString("D"),
                CorrelationId(),

                // Counts and a decision, never content — a summary quoting the draft would put generated
                // recipe text into the audit log, which AuditLog's own remarks forbid. The rewritten and
                // dropped counts are here because neither is visible from "accepted 42 of 60": one says the
                // creator replaced the words, the other says the recipe could not hold them.
                $"{model.Decision} — {accepted.Count} of {changes.Count} parts accepted, "
                    + $"{Rewritten(composition)} rewritten by the creator, "
                    + $"{Dropped(composition)} with nowhere to be created, "
                    + $"{CautionedCount(proposal, acceptedIds)} carrying a safety caution.",
                BeforeReference: AiOperationStatus.Proposed.ToString(),
                AfterReference: status.ToString()),
            Feedback(proposal.Id, model));

        var write = await operations.AcceptDraftAsync(
            requestId,
            instruction,

            // Closes over the composed draft so the recipe facade is called from here — Business is the only
            // layer permitted to reach another module — while the transaction around it belongs to the
            // DataLayer, which is the layer that owns transaction boundaries.
            token => CreateAsync(proposal.Id, composition?.Draft, token),
            carriesEdits: edits.Count > 0,
            cancellationToken);

        return write.Outcome switch
        {
            AiDispositionOutcome.Applied or AiDispositionOutcome.Replayed =>
                OperationResult<AiDraftAcceptanceServiceModel>.Success(new AiDraftAcceptanceServiceModel(
                    requestId,
                    status,
                    write.RecipeId,
                    write.RecipeVersionNumber,
                    accepted.Count,
                    changes.Count - accepted.Count,
                    Rewritten(composition),
                    Dropped(composition),
                    Replayed: write.Outcome is AiDispositionOutcome.Replayed,
                    clock.UtcNow)),

            AiDispositionOutcome.NotFound => Failure(
                AiFirstDraftRequestErrors.RequestNotFound, "That recipe draft request does not exist."),

            // The recipe domain's own refusal, passed through unchanged: it names the field at fault, which is
            // more than this seam could say about it.
            AiDispositionOutcome.ApplyRefused =>
                OperationResult<AiDraftAcceptanceServiceModel>.Failure(write.Error!),

            _ => Failure(
                AiDraftAcceptanceErrors.DraftDecided,
                "That draft has already been decided, and a decision cannot be changed."),
        };
    }

    /// <summary>
    /// Creates the recipe through the recipe module's facade, or nothing at all for a rejection.
    /// </summary>
    /// <remarks>
    /// The facade, never anything below it. Every rule a recipe a creator typed obeys therefore applies — the
    /// role check, the create validator, the cross-module reference checks, the recipe's own invariants. A
    /// model's output cannot reach a recipe by a shorter route (AIREC-GR-007).
    /// </remarks>
    private async Task<OperationResult<AiCreatedRecipe?>> CreateAsync(
        Guid proposalId,
        Domain.Modules.Recipes.Managers.ProposedRecipeDraft? draft,
        CancellationToken cancellationToken)
    {
        if (draft is null)
        {
            return OperationResult<AiCreatedRecipe?>.Success(null);
        }

        var created = await recipes.CreateFromProposalAsync(proposalId, draft, cancellationToken);

        return created.Succeeded
            ? OperationResult<AiCreatedRecipe?>.Success(
                new AiCreatedRecipe(created.Value!.RecipeId, created.Value.VersionNumber))
            : OperationResult<AiCreatedRecipe?>.Failure(created.Error!);
    }

    /// <summary>
    /// Whether the confirmed selection is one this draft can honour.
    /// </summary>
    /// <remarks>
    /// Refuses before anything is written, for the reason the disposition seam gives: an invalid selection must
    /// not apply the part of itself that was valid. The shape checks belong to the validator; what is checked
    /// here needs the stored proposal.
    /// </remarks>
    private static OperationResult<AiDraftAcceptanceServiceModel>? Select(
        AiProposal proposal,
        AiDispositionDecision decision,
        IReadOnlyList<Guid> accepted)
    {
        var known = proposal.Changes.Select(change => change.Id).ToHashSet();

        if (accepted.Any(id => !known.Contains(id)))
        {
            return Failure(
                AiDraftAcceptanceErrors.SelectionInvalid,
                "One of the accepted parts does not belong to this draft.");
        }

        // What makes AcceptAll a confirmation rather than a flag. A client naming fewer parts than the draft
        // holds is looking at something other than this draft, and creating a recipe on its word would build
        // one out of content nobody reviewed.
        if (decision is AiDispositionDecision.AcceptAll && accepted.Count != proposal.Changes.Count)
        {
            return Failure(
                AiDraftAcceptanceErrors.SelectionInvalid,
                $"Accepting the whole draft means naming all {proposal.Changes.Count} of its parts. Name them, "
                    + "or accept a selection instead.");
        }

        return null;
    }

    /// <summary>
    /// Indexes the creator's rewrites, refusing any that does not address something it could replace.
    /// </summary>
    /// <remarks>
    /// The check the validator cannot make, because it needs the stored proposal. A rewrite naming a change
    /// this draft does not contain, a field that change's row cannot carry, or a change the creator did not
    /// accept, is refused with nothing written — the alternative is a creator's words landing in a recipe
    /// field they were never looking at.
    /// </remarks>
    private static OperationResult<AiDraftAcceptanceServiceModel>? ReadEdits(
        IReadOnlyList<AiStructuredChange> changes,
        IReadOnlySet<Guid> accepted,
        IReadOnlyList<AiDraftFieldEditViewModel>? submitted,
        out Dictionary<(Guid ChangeId, string Field), string> edits)
    {
        edits = new Dictionary<(Guid, string), string>();

        if (submitted is null || submitted.Count == 0)
        {
            return null;
        }

        var byId = changes.ToDictionary(change => change.Id);

        foreach (var edit in submitted)
        {
            if (edit.Field is not { } field || edit.Value is not { } value)
            {
                return Failure(
                    AiDraftAcceptanceErrors.SelectionInvalid, "A rewrite must name a field and a value.");
            }

            if (!byId.TryGetValue(edit.ChangeId, out var change))
            {
                return Failure(
                    AiDraftAcceptanceErrors.SelectionInvalid,
                    "One of the rewrites does not belong to this draft.");
            }

            // Refused rather than ignored. A rewrite of something the creator did not accept is a client
            // that has lost track of its own request, and silently dropping the words they typed is the one
            // outcome worse than telling them.
            if (!accepted.Contains(edit.ChangeId))
            {
                return Failure(
                    AiDraftAcceptanceErrors.SelectionInvalid,
                    "One of the rewrites is for a part of the draft you are not accepting.");
            }

            if (!AiRecipeDraftComposer.CanEdit(change, field))
            {
                return Failure(
                    AiDraftAcceptanceErrors.SelectionInvalid,
                    $"'{field}' is not something that part of the draft can carry.");
            }

            edits[(edit.ChangeId, field)] = value;
        }

        return null;
    }

    private static int Rewritten(AiDraftComposition? composition) => composition?.RewrittenChangeIds.Count ?? 0;

    private static int Dropped(AiDraftComposition? composition) => composition?.DroppedChangeIds.Count ?? 0;

    /// <inheritdoc cref="AiProposalBusiness"/>
    private static int CautionedCount(AiProposal proposal, IReadOnlySet<Guid> accepted) =>
        proposal.Warnings
            .Where(warning => warning.Kind is AiWarningKind.SafetyCaution)
            .Select(warning => warning.AiStructuredChangeId)
            .Where(changeId => changeId is { } id && accepted.Contains(id))
            .Distinct()
            .Count();

    private AiProposalFeedback? Feedback(Guid proposalId, AiDraftAcceptanceViewModel model)
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

    /// <summary>The ambient trace, so an audit entry can be tied to the request that produced it.</summary>
    private static Guid CorrelationId()
    {
        var traceId = Activity.Current?.TraceId;

        return traceId is { } id && id != default
            ? Guid.ParseExact(id.ToHexString(), "N")
            : Guid.NewGuid();
    }

    private static OperationResult<AiDraftAcceptanceServiceModel> Failure(string code, string message) =>
        OperationResult<AiDraftAcceptanceServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
