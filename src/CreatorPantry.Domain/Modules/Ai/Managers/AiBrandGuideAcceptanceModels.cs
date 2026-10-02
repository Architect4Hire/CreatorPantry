using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// One piece of proposed guidance the creator rewrote before accepting it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It carries a value, and the reason is the one <see cref="AiDraftFieldEditViewModel"/> gives.</strong>
/// <see cref="AiProposalDispositionViewModel"/> is ids and nothing else because its changes are applied to a
/// recipe the creator already owns, where a client-supplied value could overwrite their work with something
/// nobody reviewed. Here the whole artifact is a proposal: rewriting a paragraph before accepting it is what the
/// review step is <em>for</em>, and the creator's own words are the ones content.md would rather have in their
/// guide than a model's.
/// </para>
/// <para>
/// <strong>No field name, unlike the draft's rewrite.</strong> An item of a brand-guide proposal is one piece of
/// prose; its dimension, evidence basis, channel and citations are facts about where that prose came from, not
/// alternative things to write. There is therefore exactly one thing a rewrite can mean, and a field name could
/// only ever name it or be wrong. A rewrite addressing a metadata row, a row the creator is not accepting, or a
/// finding — which becomes no guidance at all — is refused rather than ignored.
/// </para>
/// </remarks>
public sealed class AiBrandGuideEditViewModel
{
    /// <summary>The item being rewritten, named by the <c>changeId</c> the proposal published for its text.</summary>
    public Guid ChangeId { get; set; }

    /// <summary>The creator's own words. Never blank: clearing a section is not a rewrite.</summary>
    public string? Value { get; set; }
}

/// <summary>
/// The body of <c>POST .../brand-guide-proposal-requests/{requestId}/acceptance</c>: a creator deciding what to
/// do with brand guidance they have reviewed (11A.18).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Selection is by item, not by row.</strong> The proposal stores each piece of guidance as one
/// <see cref="AiChangeKind.Add"/> row carrying its text, with its dimension, channel, evidence basis and
/// citations as <see cref="AiChangeKind.Set"/> rows against the same target id. Those are the shape of the item
/// a creator is looking at rather than separate propositions, so naming the text row takes the whole item and
/// the server marks its companions accepted with it. Naming a companion on its own is refused: an attribute of
/// something you are not taking is not a thing to take.
/// </para>
/// <para>
/// <strong>Accepting writes one new draft version of the guide and nothing else.</strong> It does not approve
/// the version and does not make it the workspace default; both are later, separate decisions, and the second
/// needs an Owner.
/// </para>
/// <para>
/// <strong>No idempotency key, and retrying is safe without one.</strong> A proposal that has already been
/// decided is recognised inside the acceptance transaction from the operation's own terminal status, and a retry
/// of the same decision writes nothing a second time — no second version. A retry asking for a
/// <em>different</em> decision is refused.
/// </para>
/// <para>
/// <strong>Named for the verb rather than after its reply, and that is load-bearing.</strong>
/// <c>OpenApiDocumentation.StripLayerSuffix</c> drops both <c>ViewModel</c> and <c>ServiceModel</c>, so a
/// request called <c>AiBrandGuideAcceptanceViewModel</c> and a reply called
/// <see cref="AiBrandGuideAcceptanceServiceModel"/> would claim the same schema id — and the published document
/// would describe the reply's shape as the request body, which is what the snapshot showed when this type was
/// first written. The verb-first spelling matches <see cref="RequestBrandGuideProposalViewModel"/> and the
/// brand module's own request models, and it keeps the two schemas apart.
/// </para>
/// </remarks>
public sealed class AcceptBrandGuideProposalViewModel
{
    /// <summary>Accept every item, accept a selection, or reject.</summary>
    public AiDispositionDecision Decision { get; set; }

    /// <summary>
    /// The items being accepted, named by the <c>changeId</c> the proposal published for each one's text.
    /// Required for both accept decisions, and must be empty for a rejection.
    /// </summary>
    public IReadOnlyList<Guid>? AcceptedChangeIds { get; set; }

    /// <summary>The creator's own wording for guidance they rewrote while reviewing. Optional.</summary>
    public IReadOnlyList<AiBrandGuideEditViewModel>? Edits { get; set; }

    /// <summary>The creator's note on why the guide changed, stored on the new version. Optional.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Whether the proposal was useful. Optional, and independent of the decision.</summary>
    public bool? WasHelpful { get; set; }

    /// <summary>The creator's own words about the proposal. Optional.</summary>
    public string? Comment { get; set; }
}

/// <remarks>
/// Mirrors <see cref="AiDraftAcceptanceViewModelValidator"/> on the decision, the selection and the shape of a
/// rewrite. What a rewrite may <em>address</em> needs the stored proposal, so Business checks that and refuses
/// with nothing written.
/// </remarks>
public sealed class AcceptBrandGuideProposalViewModelValidator : AbstractValidator<AcceptBrandGuideProposalViewModel>
{
    public AcceptBrandGuideProposalViewModelValidator()
    {
        RuleFor(model => model.Decision)
            .NotEqual(AiDispositionDecision.Unspecified)
            .WithMessage("Say whether you are accepting or rejecting this guidance.")
            .IsInEnum().WithMessage("That is not a decision this proposal accepts.");

        When(model => model.Decision is AiDispositionDecision.AcceptAll or AiDispositionDecision.AcceptSelected, () =>
        {
            RuleFor(model => model.AcceptedChangeIds)
                .NotNull().WithMessage("Name the guidance you are accepting.")
                .Must(ids => ids is null || ids.Count > 0)
                .WithMessage("Name the guidance you are accepting.")
                .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
                .WithMessage("One of the accepted items has no id.")
                .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
                .WithMessage("The same item is listed more than once.");
        });

        // A rejection that names items is a client that has not decided what it is asking for. Refusing is safer
        // than picking one of the two readings, because one of them writes to the creator's guide.
        When(model => model.Decision is AiDispositionDecision.Reject, () =>
        {
            RuleFor(model => model.AcceptedChangeIds)
                .Must(ids => ids is null || ids.Count == 0)
                .WithMessage("A rejection cannot also accept guidance.");

            RuleFor(model => model.Edits)
                .Must(edits => edits is null || edits.Count == 0)
                .WithMessage("A rejection cannot carry rewrites: no version is written to put them in.");

            RuleFor(model => model.ChangeReason)
                .Must(reason => string.IsNullOrWhiteSpace(reason))
                .WithMessage("A rejection writes no version, so there is nothing for a change reason to describe.");
        });

        RuleForEach(model => model.Edits!).ChildRules(edit =>
        {
            edit.RuleFor(item => item.ChangeId)
                .NotEqual(Guid.Empty).WithMessage("A rewrite must say which piece of guidance it replaces.");

            // Blank is refused rather than read as "leave this out". There is no way to tell a deliberate
            // clearing from a slip, and one of the two readings silently drops guidance the creator reviewed.
            edit.RuleFor(item => item.Value)
                .NotEmpty().WithMessage("Write your version, or accept the suggestion as it stands.")
                .MaximumLength(AiPolicy.ChangeValueMaxLength);
        }).When(model => model.Edits is not null);

        RuleFor(model => model.Edits!)
            .Must(edits => edits.Select(edit => edit.ChangeId).Distinct().Count() == edits.Count)
            .WithMessage("The same piece of guidance is rewritten more than once.")
            .When(model => model.Edits is not null);

        RuleFor(model => model.ChangeReason)
            .MaximumLength(AiPolicy.MessageMaxLength)
            .WithMessage($"Keep the change reason to {AiPolicy.MessageMaxLength} characters or fewer.");

        RuleFor(model => model.Comment)
            .MaximumLength(AiPolicy.MessageMaxLength)
            .WithMessage($"Keep the comment to {AiPolicy.MessageMaxLength} characters or fewer.");
    }
}

/// <summary>
/// The guide version an acceptance wrote.
/// </summary>
/// <remarks>
/// This module's own shape rather than the brand module's, which is the translation backend.md asks Business
/// for: the HTTP contract of this route should not move because another module renamed a field of its own.
/// </remarks>
/// <param name="GuideVersionId">The new version.</param>
/// <param name="GuideVersionNumber">Its number, one past the version it was written from.</param>
/// <param name="ParentVersionNumber">The working version the accepted guidance was laid over.</param>
/// <param name="SectionsAdded">Accepted sections the guide had nothing under that key.</param>
/// <param name="SectionsReplaced">Accepted sections that replaced different text under a key it already had.</param>
/// <param name="RulesAdded">Accepted rules that were not already in the guide.</param>
/// <param name="RulesAlreadyPresent">
/// Accepted rules the guide already held. Reported rather than dropped quietly: a creator who ticked three rules
/// and sees one added is entitled to know why.
/// </param>
/// <param name="CitedSourceCount">How many source document versions the new version cites in total.</param>
/// <param name="StaleSourceCount">
/// How many of those the owning document has since replaced. Citations are written at the versions the proposal
/// read and are never re-pointed at a document's newer text — so this is reported rather than refused, and it is
/// what a creator needs to know before trying to activate the version.
/// </param>
public sealed record AiBrandGuideVersionServiceModel(
    Guid GuideVersionId,
    int GuideVersionNumber,
    int ParentVersionNumber,
    int SectionsAdded,
    int SectionsReplaced,
    int RulesAdded,
    int RulesAlreadyPresent,
    int CitedSourceCount,
    int StaleSourceCount);

/// <summary>What accepting brand guidance did.</summary>
/// <param name="AiProposalRequestId">The request the proposal belongs to — the id the route named.</param>
/// <param name="Status">
/// Where the operation ended up: <c>Accepted</c>, <c>PartiallyAccepted</c> or <c>Rejected</c>. All three are
/// terminal, which is why there is nothing to poll afterwards.
/// </param>
/// <param name="GuideId">The guide this proposal was about, as its stored inputs recorded it.</param>
/// <param name="Written">
/// The version this call wrote, or <c>null</c> when it wrote none.
/// </param>
/// <param name="AcceptedItemCount">How many pieces of guidance were taken.</param>
/// <param name="RejectedItemCount">How many were declined. Recorded rather than discarded.</param>
/// <param name="RewrittenItemCount">
/// How many of the accepted items the creator rewrote first. Their words went into the guide; the model's did
/// not.
/// </param>
/// <param name="DroppedItemCount">
/// Accepted items that produced no guidance: a reported conflict or an uncertainty. Both are findings about the
/// creator's material rather than things to write into a guide, and a guide has nowhere to put either — so they
/// are counted rather than silently lost, because a creator who ticked one is entitled to know it did not land.
/// </param>
/// <param name="Replayed">
/// Whether this answered a decision that had already been recorded. Nothing was written again.
/// </param>
/// <remarks>
/// <para>
/// <strong>There are three ways <paramref name="Written"/> is null, and the reply distinguishes two of
/// them.</strong> A rejection writes no version and says so through <paramref name="Status"/>. A replay writes
/// none and says so through <paramref name="Replayed"/>. The third is a no-op: every accepted item said what the
/// guide already said, so there was nothing to write — the same rule a creator's own edit follows. A client that
/// needs the version in any of the three cases reads the guide, whose id it has.
/// </para>
/// <para>
/// A replay does not name the version the first call wrote. Nothing stores which guide version an operation
/// produced, and adding a column to record it would couple an AI operation to the brand module for a value the
/// caller can already reach: unlike a creator retrying a draft acceptance, who does not yet know their recipe's
/// id, the caller here named the guide in the request that started all this.
/// </para>
/// </remarks>
public sealed record AiBrandGuideAcceptanceServiceModel(
    Guid AiProposalRequestId,
    AiOperationStatus Status,
    Guid GuideId,
    AiBrandGuideVersionServiceModel? Written,
    int AcceptedItemCount,
    int RejectedItemCount,
    int RewrittenItemCount,
    int DroppedItemCount,
    bool Replayed,
    DateTimeOffset DecidedAt);

/// <summary>Stable error codes this seam introduces. Renaming one is a breaking API change.</summary>
/// <remarks>
/// The dotted suffix selects the HTTP status — see <c>AiProposalErrors</c>' own remarks on why that is
/// load-bearing rather than styling.
/// </remarks>
public static class AiBrandGuideAcceptanceErrors
{
    /// <summary>The request failed shape validation. Falls through to 400.</summary>
    public const string RequestInvalid = "ai.brandGuideAcceptance.invalid_request";

    /// <summary>
    /// The selection named something this proposal cannot honour, or a rewrite addressed something it cannot
    /// replace. Nothing is written. Falls through to 400.
    /// </summary>
    public const string SelectionInvalid = "ai.brandGuideSelection.invalid_request";

    /// <summary>The request exists but has produced no guidance to decide about — still queued, or it failed. Maps to 404.</summary>
    public const string ProposalNotFound = "ai.brandGuideProposalOutput.not_found";

    /// <summary>
    /// The proposal has already been decided differently, or never reached a state where it could be decided.
    /// Terminal states do not reopen, so this is 409 rather than a validation failure.
    /// </summary>
    public const string ProposalDecided = "ai.brandGuideProposal.conflict";

    /// <summary>
    /// The stored proposal cannot be acted on: its recorded inputs do not name a guide and a version. Maps to 422.
    /// </summary>
    /// <remarks>
    /// Unreachable through any request this server accepts — the request seam writes both fields — so this is
    /// the answer for a row written before those fields existed or by a future writer that forgets one. An
    /// honest refusal rather than an exception inside a transaction, because the stored row is not the creator's
    /// fault either way.
    /// </remarks>
    public const string ProposalUnreadable = "ai.brandGuideProposal.unprocessable";

    /// <summary>The caller belongs to the workspace but their role may not write guide versions in it. Maps to 403.</summary>
    public const string AcceptanceForbidden = "ai.brandGuideAcceptance.forbidden";
}
