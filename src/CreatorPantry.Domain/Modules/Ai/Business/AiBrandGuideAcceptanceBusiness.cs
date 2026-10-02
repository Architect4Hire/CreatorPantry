using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiBrandGuideAcceptanceBusiness
{
    /// <summary>
    /// Records what the creator decided about proposed brand guidance, writing the guide version they accepted
    /// it into.
    /// </summary>
    Task<OperationResult<AiBrandGuideAcceptanceServiceModel>> AcceptAsync(
        string actorUserId,
        Guid requestId,
        AcceptBrandGuideProposalViewModel model,
        CancellationToken cancellationToken);
}

/// <summary>
/// The rules around accepting 11A.17's brand-guide proposal into a new draft version of the guide (11A.18).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only thing in the system that turns proposed guidance into a brand guide.</strong>
/// Nothing else writes a guide version from a proposal, and this writes at most one — inside the transaction
/// <see cref="IAiOperationDataLayer.AcceptBrandGuideProposalAsync"/> owns, through the brand module's own
/// facade, which is the only route a module may take into another.
/// </para>
/// <para>
/// <strong>What it does not do.</strong> It does not approve the version, does not touch the workspace default,
/// and does not rebase: a guide edited since the proposal was composed is refused rather than written over, and
/// a cited source document replaced since is cited at the version the proposal read rather than re-pointed at
/// the newer one. Those are the three ways accepted guidance could silently become something the creator never
/// reviewed.
/// </para>
/// <para>
/// <strong>Selection is item-level.</strong> The proposal stores each piece of guidance as one
/// <see cref="AiChangeKind.Add"/> row with its metadata as <see cref="AiChangeKind.Set"/> rows against the same
/// target id, so the creator names item rows and this expands the selection onto their companions before
/// anything is recorded — otherwise the stored record would say a section was taken and its own dimension
/// declined.
/// </para>
/// </remarks>
internal sealed class AiBrandGuideAcceptanceBusiness(
    IAiOperationDataLayer operations,
    IBrandStyleGuideFacade guides,
    IBrandSourcePassageFacade passages,
    IWorkspaceContext workspace,
    IClock clock) : IAiBrandGuideAcceptanceBusiness
{
    public async Task<OperationResult<AiBrandGuideAcceptanceServiceModel>> AcceptAsync(
        string actorUserId,
        Guid requestId,
        AcceptBrandGuideProposalViewModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Checked here, not only at the controller policy: this boundary is also reachable by a worker or a
        // plugin, which no MVC policy protects. The brand facade checks it again — it has to, it is a separate
        // module — and this is so the refusal names deciding rather than writing.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Failure(
                AiBrandGuideAcceptanceErrors.AcceptanceForbidden,
                "You do not have permission to write brand style guide versions in this workspace.");
        }

        var loaded = await operations.GetForDispositionAsync(requestId, cancellationToken);

        // The same absence for an operation in another workspace, one that ran a different task, and one that
        // does not exist. None of the three discloses anything about the others (tenancy.md).
        if (loaded is null || loaded.Operation.TaskType != AiTaskType.BrandGuideProposal)
        {
            return Failure(
                AiBrandGuideProposalRequestErrors.RequestNotFound,
                "That brand guide proposal request does not exist.");
        }

        if (loaded.Proposal is not { } proposal)
        {
            return Failure(
                AiBrandGuideAcceptanceErrors.ProposalNotFound,
                "That request has not produced guidance to decide about.");
        }

        if (loaded.Operation.Status is not AiOperationStatus.Proposed
            && !AiOperationTransitionPolicy.IsTerminal(loaded.Operation.Status))
        {
            // Still queued or running. Its own answer, because waiting is the remedy — unlike a terminal
            // operation, where nothing the creator does will make it decidable again.
            return Failure(
                AiBrandGuideAcceptanceErrors.ProposalDecided,
                "That guidance is not ready to be decided about yet.");
        }

        var (guideId, workingVersionNumber) = ReadTarget(loaded.Operation);

        if (guideId == Guid.Empty)
        {
            // Unreachable through any request this server accepts — see the error's own remarks.
            return Failure(
                AiBrandGuideAcceptanceErrors.ProposalUnreadable,
                "That proposal does not record which guide and version it was written against, so it cannot be "
                    + "accepted. Ask for it again.");
        }

        var items = AiBrandGuideAcceptanceComposer.Items(proposal.Changes);

        IReadOnlyList<Guid> named = model.Decision is AiDispositionDecision.Reject
            ? []
            : model.AcceptedChangeIds ?? [];

        if (Select(items, proposal, model.Decision, named) is { } selectionError)
        {
            return selectionError;
        }

        var acceptedItems = named.ToHashSet();

        if (ReadEdits(items, acceptedItems, model.Edits, out var edits) is { } editError)
        {
            return editError;
        }

        // Composed before the transaction opens. It reads only stored rows and the creator's own edits and makes
        // no database call, so nothing it does has to be rolled back.
        var composition = AiBrandGuideAcceptanceComposer.Compose(items, acceptedItems, edits);

        // Two accepted items writing the same section would have one quietly overwrite the other in the merge.
        // Refused instead: the creator reviewed two pieces of guidance and is entitled to have both land or to
        // be told which to choose.
        if (composition.Sections
            .GroupBy(section => (section.SectionKey, section.ChannelKey ?? string.Empty))
            .FirstOrDefault(group => group.Count() > 1) is { } clash)
        {
            return Failure(
                AiBrandGuideAcceptanceErrors.SelectionInvalid,
                $"You have accepted more than one piece of guidance for the {clash.Key.SectionKey} section. "
                    + "A guide holds one of each, so accept the one you want.");
        }

        // Every change of the proposal is decided, so the companions of an accepted item are accepted with it.
        // Without this, the stored record would say a section was taken and its own dimension declined.
        var acceptedChanges = acceptedItems
            .Concat(items.Where(item => acceptedItems.Contains(item.ItemId)).SelectMany(item => item.CompanionIds))
            .ToHashSet();

        var status = acceptedItems.Count == 0
            ? AiOperationStatus.Rejected
            : acceptedItems.Count == items.Count
                ? AiOperationStatus.Accepted
                : AiOperationStatus.PartiallyAccepted;

        // Asked rather than assumed, the same way the other decision seams ask: the policy is the single source
        // of which moves are legal, and a status computed here that it would refuse is a bug worth failing on.
        if (!AiOperationTransitionPolicy.IsAllowed(AiOperationStatus.Proposed, status))
        {
            throw new InvalidOperationException(
                $"A brand guide acceptance computed a status an AI operation cannot move to: {status}.");
        }

        var instruction = new AiDispositionInstruction(
            acceptedChanges,
            status,
            workspace.MembershipId,
            new AuditEntry(
                actorUserId,
                AiOperationTransitionPolicy.AuditAction(AiOperationStatus.Proposed, status),
                AiAuditResources.Proposal,
                proposal.Id.ToString("D"),
                CorrelationId(),

                // Counts and a decision, never content — a summary quoting the guidance would put generated text
                // about a creator's private brand voice into the audit log, which AuditLog's own remarks forbid.
                // The rewritten and dropped counts are here because neither is visible from "accepted 6 of 9":
                // one says the creator replaced the words, the other says a guide had nowhere to put them.
                $"{model.Decision} — {acceptedItems.Count} of {items.Count} items accepted, "
                    + $"{composition.RewrittenItemIds.Count} rewritten by the creator, "
                    + $"{composition.DroppedItemIds.Count} with nowhere in a guide to go, "
                    + $"{CautionedCount(proposal, acceptedItems)} carrying a safety caution.",
                BeforeReference: AiOperationStatus.Proposed.ToString(),
                AfterReference: status.ToString()),
            Feedback(proposal.Id, model));

        var write = await operations.AcceptBrandGuideProposalAsync(
            requestId,
            instruction,

            // Closes over the composed guidance so the brand facade is called from here — Business is the only
            // layer permitted to reach another module — while the transaction around it belongs to the
            // DataLayer, which is the layer that owns transaction boundaries.
            token => WriteAsync(
                actorUserId, guideId, workingVersionNumber, proposal.Id, composition, model.ChangeReason, token),
            carriesEdits: edits.Count > 0,
            cancellationToken);

        return write.Outcome switch
        {
            AiDispositionOutcome.Applied or AiDispositionOutcome.Replayed =>
                OperationResult<AiBrandGuideAcceptanceServiceModel>.Success(new AiBrandGuideAcceptanceServiceModel(
                    requestId,
                    status,
                    guideId,
                    Describe(write.Written),
                    acceptedItems.Count,
                    items.Count - acceptedItems.Count,
                    composition.RewrittenItemIds.Count,
                    composition.DroppedItemIds.Count,
                    Replayed: write.Outcome is AiDispositionOutcome.Replayed,
                    clock.UtcNow)),

            AiDispositionOutcome.NotFound => Failure(
                AiBrandGuideProposalRequestErrors.RequestNotFound,
                "That brand guide proposal request does not exist."),

            // The brand domain's own refusal, passed through unchanged. A guide edited since the guidance was
            // composed arrives here as brand.guide.workingVersion.conflict, and that is the right answer to
            // give: the remedy is a brand remedy — read the guide again and ask for the proposal again — so
            // translating the code into an AI one would only obscure where to look.
            AiDispositionOutcome.ApplyRefused =>
                OperationResult<AiBrandGuideAcceptanceServiceModel>.Failure(write.Error!),

            _ => Failure(
                AiBrandGuideAcceptanceErrors.ProposalDecided,
                "That guidance has already been decided, and a decision cannot be changed."),
        };
    }

    /// <summary>
    /// Writes the accepted guidance through the brand module's facade, or nothing at all for a rejection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The facade, never anything below it. Every rule a version the creator typed obeys therefore applies — the
    /// role check, the archived-guide refusal, the section and rule bounds, the caps, the requirement that each
    /// citation resolve to an exact version this workspace owns, and the working-version check that stops a
    /// rebase. A model's output cannot reach a guide by a shorter route.
    /// </para>
    /// <para>
    /// <strong>The citations are turned into source versions here, and by the brand module.</strong> A citation
    /// names a passage; a guide version's provenance names a document version. Only the brand module can map
    /// between them, so it is asked — rather than this module assuming that every document the proposal was
    /// grounded in backs every piece of guidance the creator took.
    /// </para>
    /// </remarks>
    private async Task<OperationResult<BrandStyleGuideVersionCreatedServiceModel?>> WriteAsync(
        string actorUserId,
        Guid guideId,
        int workingVersionNumber,
        Guid proposalId,
        AiBrandGuideComposition composition,
        string? changeReason,
        CancellationToken cancellationToken)
    {
        if (composition.Sections.Count == 0 && composition.Rules.Count == 0)
        {
            // A rejection, or an acceptance of findings alone. Either way there is no guidance to write, and
            // writing a version holding exactly what the last one held would make the history harder to read.
            return OperationResult<BrandStyleGuideVersionCreatedServiceModel?>.Success(null);
        }

        var origins = await passages.ResolveOriginsAsync(composition.CitedPassageIds, cancellationToken);

        var application = new BrandStyleGuideProposalApplication(
            proposalId,
            workingVersionNumber,
            composition.Sections,
            composition.Rules,

            // Distinct because two sections may cite the same document version, and a guide cites a version once.
            // A passage the brand module could not resolve simply contributes nothing: provenance for a passage
            // that is no longer readable cannot be written, and refusing the whole acceptance over a footnote
            // would cost the creator the guidance they came for.
            [
                .. origins
                    .Select(origin => new BrandStyleGuideSourceServiceModel(origin.DocumentId, origin.VersionNumber))
                    .Distinct(),
            ],
            string.IsNullOrWhiteSpace(changeReason) ? null : changeReason.Trim());

        // The actor travels because the brand facade writes its own audit entry for the version, beside the one
        // this seam writes for the decision. Two entries, one actor: "who decided" and "who wrote the version"
        // are the same person here, and a record that could not say so would be the weaker one.
        var written = await guides.CreateVersionFromProposalAsync(
            actorUserId,
            guideId,
            application,
            cancellationToken);

        return written.Succeeded
            ? OperationResult<BrandStyleGuideVersionCreatedServiceModel?>.Success(written.Value)
            : OperationResult<BrandStyleGuideVersionCreatedServiceModel?>.Failure(written.Error!);
    }

    /// <summary>The guide and working version the proposal was written against, from the operation's own inputs.</summary>
    /// <remarks>
    /// Read back from <c>TaskInputsJson</c> rather than taken from the request, which has no field for either.
    /// A client cannot name the guide a proposal is accepted into, and so cannot aim generated guidance at a
    /// guide the proposal was never about.
    /// </remarks>
    private static (Guid GuideId, int WorkingVersionNumber) ReadTarget(AiOperation operation)
    {
        Dictionary<string, string>? inputs;

        try
        {
            inputs = operation.TaskInputsJson is null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson);
        }
        catch (JsonException)
        {
            // A stored row this seam cannot read. An answer, not an exception: the row is not the creator's
            // fault, and the caller turns this into a refusal that tells them to ask again.
            return (Guid.Empty, 0);
        }

        if (inputs is null
            || !inputs.TryGetValue(AiBrandGuideProposalInputs.GuideId, out var rawGuide)
            || !Guid.TryParse(rawGuide, out var guideId)
            || !inputs.TryGetValue(AiBrandGuideProposalInputs.GuideVersionNumber, out var rawVersion)
            || !int.TryParse(rawVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber)
            || versionNumber <= 0)
        {
            return (Guid.Empty, 0);
        }

        return (guideId, versionNumber);
    }

    /// <summary>
    /// Whether the confirmed selection is one this proposal can honour.
    /// </summary>
    /// <remarks>
    /// Refuses before anything is written, for the reason the other decision seams give: an invalid selection
    /// must not apply the part of itself that was valid. The shape checks belong to the validator; what is
    /// checked here needs the stored proposal.
    /// </remarks>
    private static OperationResult<AiBrandGuideAcceptanceServiceModel>? Select(
        IReadOnlyList<AiBrandGuideProposedItem> items,
        AiProposal proposal,
        AiDispositionDecision decision,
        IReadOnlyList<Guid> named)
    {
        var selectable = items.Select(item => item.ItemId).ToHashSet();

        if (named.Any(id => !selectable.Contains(id)))
        {
            // Covers both an id this proposal does not contain and a metadata row named on its own. The second
            // gets the fuller message, because a client that sent one has misread the shape rather than the
            // proposal.
            var companions = items.SelectMany(item => item.CompanionIds).ToHashSet();

            return Failure(
                AiBrandGuideAcceptanceErrors.SelectionInvalid,
                named.Any(companions.Contains)
                    ? "Accept guidance by naming the change that carries its text. The rows describing it — its "
                        + "dimension, channel, evidence and citations — are accepted with it."
                    : "One of the accepted items does not belong to this proposal.");
        }

        // What makes AcceptAll a confirmation rather than a flag. A client naming fewer items than the proposal
        // holds is looking at something other than this proposal, and writing a guide version on its word would
        // build one out of guidance nobody reviewed.
        if (decision is AiDispositionDecision.AcceptAll && named.Count != items.Count)
        {
            return Failure(
                AiBrandGuideAcceptanceErrors.SelectionInvalid,
                $"Accepting everything means naming all {items.Count} items. Name them, or accept a selection "
                    + "instead.");
        }

        // A proposal whose rows produced no items at all. Nothing is selectable, so an accept cannot be
        // honoured — and a rejection still can, which is why this is checked after the selection rather than
        // before it.
        return items.Count == 0 && decision is not AiDispositionDecision.Reject
            ? Failure(
                AiBrandGuideAcceptanceErrors.ProposalNotFound,
                "That proposal holds no guidance to accept.")
            : null;
    }

    /// <summary>
    /// Indexes the creator's rewrites, refusing any that does not address something it could replace.
    /// </summary>
    /// <remarks>
    /// The check the validator cannot make, because it needs the stored proposal. A rewrite naming an item this
    /// proposal does not contain, an item the creator did not accept, or one whose text becomes no guidance, is
    /// refused with nothing written — the alternative is a creator's words being taken and discarded while the
    /// reply reports success.
    /// </remarks>
    private static OperationResult<AiBrandGuideAcceptanceServiceModel>? ReadEdits(
        IReadOnlyList<AiBrandGuideProposedItem> items,
        IReadOnlySet<Guid> accepted,
        IReadOnlyList<AiBrandGuideEditViewModel>? submitted,
        out Dictionary<Guid, string> edits)
    {
        edits = [];

        if (submitted is null || submitted.Count == 0)
        {
            return null;
        }

        var byId = items.ToDictionary(item => item.ItemId);

        foreach (var edit in submitted)
        {
            if (edit.Value is not { } value)
            {
                return Failure(
                    AiBrandGuideAcceptanceErrors.SelectionInvalid, "A rewrite must carry the text to use.");
            }

            if (!byId.TryGetValue(edit.ChangeId, out var item))
            {
                return Failure(
                    AiBrandGuideAcceptanceErrors.SelectionInvalid,
                    "One of the rewrites does not belong to this proposal.");
            }

            // Refused rather than ignored. A rewrite of something the creator did not accept is a client that
            // has lost track of its own request, and silently dropping the words they typed is the one outcome
            // worse than telling them.
            if (!accepted.Contains(edit.ChangeId))
            {
                return Failure(
                    AiBrandGuideAcceptanceErrors.SelectionInvalid,
                    "One of the rewrites is for guidance you are not accepting.");
            }

            if (!AiBrandGuideAcceptanceComposer.CanEdit(item))
            {
                return Failure(
                    AiBrandGuideAcceptanceErrors.SelectionInvalid,
                    "One of the rewrites is for a reported conflict or uncertainty. Those are findings about "
                        + "your own material rather than guidance, so a guide has nowhere to put them — "
                        + "rewritten or not.");
            }

            edits[edit.ChangeId] = value;
        }

        return null;
    }

    /// <summary>This module's own description of the version the brand module wrote, or null when none was.</summary>
    private static AiBrandGuideVersionServiceModel? Describe(BrandStyleGuideVersionCreatedServiceModel? written) =>
        written is { VersionId: { } versionId, VersionNumber: { } versionNumber }
            ? new AiBrandGuideVersionServiceModel(
                versionId,
                versionNumber,
                written.ParentVersionNumber,
                written.SectionsAdded,
                written.SectionsReplaced,
                written.RulesAdded,
                written.RulesAlreadyPresent,
                written.SourceCount,
                written.StaleSourceCount)
            : null;

    /// <inheritdoc cref="AiProposalBusiness"/>
    private static int CautionedCount(AiProposal proposal, IReadOnlySet<Guid> accepted) =>
        proposal.Warnings
            .Where(warning => warning.Kind is AiWarningKind.SafetyCaution)
            .Select(warning => warning.AiStructuredChangeId)
            .Where(changeId => changeId is { } id && accepted.Contains(id))
            .Distinct()
            .Count();

    private AiProposalFeedback? Feedback(Guid proposalId, AcceptBrandGuideProposalViewModel model)
    {
        var comment = model.Comment?.Trim();

        if (model.WasHelpful is null && string.IsNullOrEmpty(comment))
        {
            // Null rather than an empty row: a check constraint refuses feedback that says nothing, and without
            // this every acceptance would leave a row recording only that someone opened the panel.
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

    private static OperationResult<AiBrandGuideAcceptanceServiceModel> Failure(string code, string message) =>
        OperationResult<AiBrandGuideAcceptanceServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
