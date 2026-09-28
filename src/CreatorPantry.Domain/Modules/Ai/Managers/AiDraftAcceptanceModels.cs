using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// One value the creator rewrote before accepting it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the one place an AI decision contract carries a value, and the difference from
/// <see cref="AiProposalDispositionViewModel"/> is the whole reason.</strong> There, the contract is ids and
/// nothing else, because the changes are being applied to a recipe the creator already owns and a
/// client-supplied value could overwrite their work with something nobody reviewed. Here there is no recipe
/// yet — the whole artifact is a proposal, and rewriting part of it before accepting is what the review step
/// is for. A value smuggled in can only end up in a recipe that would not otherwise have existed.
/// </para>
/// <para>
/// Every edit is still checked against the stored proposal: <see cref="ChangeId"/> must name a change this
/// proposal contains, and <see cref="Field"/> must be one that change's row can carry. An edit naming
/// anything else is refused with nothing written.
/// </para>
/// </remarks>
public sealed class AiDraftFieldEditViewModel
{
    /// <summary>The change being rewritten, named by the <c>changeId</c> the proposal published.</summary>
    public Guid ChangeId { get; set; }

    /// <summary>
    /// Which field of that row — <c>displayText</c>, <c>text</c>, or a recipe-level field name.
    /// </summary>
    public string? Field { get; set; }

    /// <summary>The creator's own words. Never blank: clearing a field is not a rewrite.</summary>
    public string? Value { get; set; }
}

/// <summary>
/// The body of <c>POST .../recipe-draft-requests/{requestId}/acceptance</c>: a creator deciding what to do
/// with a generated first draft they have reviewed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Accepting is the only thing that creates a recipe.</strong> Nothing else in this module writes one,
/// and this route writes exactly one — the recipe, its version 1, the per-change dispositions and the
/// operation's terminal status all commit together or none of them do.
/// </para>
/// <para>
/// <strong>No idempotency key, deliberately.</strong> A replay is recognised by the operation's own terminal
/// status, re-read inside the transaction, and the recipe it produced is recorded on the operation — so a
/// retried request is answered with the recipe the first one created rather than a second recipe.
/// </para>
/// </remarks>
public sealed class AiDraftAcceptanceViewModel
{
    /// <summary>Accept everything, accept a selection, or reject.</summary>
    public AiDispositionDecision Decision { get; set; }

    /// <summary>
    /// The changes being accepted, named by the <c>changeId</c> the proposal published. Required for both
    /// accept decisions, and must be empty for a rejection.
    /// </summary>
    public IReadOnlyList<Guid>? AcceptedChangeIds { get; set; }

    /// <summary>The creator's own wording for values they rewrote while reviewing. Optional.</summary>
    public IReadOnlyList<AiDraftFieldEditViewModel>? Edits { get; set; }

    /// <summary>Whether the draft was useful. Optional, and independent of the decision.</summary>
    public bool? WasHelpful { get; set; }

    /// <summary>The creator's own words about the draft. Optional.</summary>
    public string? Comment { get; set; }
}

/// <remarks>
/// Mirrors <see cref="AiProposalDispositionViewModelValidator"/> on the decision and selection, and adds the
/// shape rules for edits. What an edit may <em>address</em> is not checkable here — that needs the stored
/// proposal — so Business checks it and refuses with nothing written.
/// </remarks>
public sealed class AiDraftAcceptanceViewModelValidator : AbstractValidator<AiDraftAcceptanceViewModel>
{
    public AiDraftAcceptanceViewModelValidator()
    {
        RuleFor(model => model.Decision)
            .NotEqual(AiDispositionDecision.Unspecified)
            .WithMessage("Say whether you are accepting or rejecting this draft.")
            .IsInEnum().WithMessage("That is not a decision this draft accepts.");

        When(model => model.Decision is AiDispositionDecision.AcceptAll or AiDispositionDecision.AcceptSelected, () =>
        {
            RuleFor(model => model.AcceptedChangeIds)
                .NotNull().WithMessage("Name the parts of the draft you are accepting.")
                .Must(ids => ids is null || ids.Count > 0)
                .WithMessage("Name the parts of the draft you are accepting.")
                .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
                .WithMessage("One of the accepted parts has no id.")
                .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
                .WithMessage("The same part is listed more than once.");
        });

        // A rejection that names changes is a client that has not decided what it is asking for. Refusing is
        // safer than picking one of the two readings, because one of them creates a recipe.
        When(model => model.Decision is AiDispositionDecision.Reject, () =>
        {
            RuleFor(model => model.AcceptedChangeIds)
                .Must(ids => ids is null || ids.Count == 0)
                .WithMessage("A rejection cannot also accept parts of the draft.");

            RuleFor(model => model.Edits)
                .Must(edits => edits is null || edits.Count == 0)
                .WithMessage("A rejection cannot carry rewrites: nothing is created to put them in.");
        });

        RuleForEach(model => model.Edits!).ChildRules(edit =>
        {
            edit.RuleFor(item => item.ChangeId)
                .NotEqual(Guid.Empty).WithMessage("A rewrite must say which part of the draft it replaces.");

            edit.RuleFor(item => item.Field)
                .NotEmpty().WithMessage("A rewrite must say which field it replaces.")
                .MaximumLength(AiPolicy.FieldNameMaxLength);

            // Blank is refused rather than read as "clear this". There is no way to tell a deliberate
            // clearing from a slip, and one of the two readings silently drops content the creator reviewed.
            edit.RuleFor(item => item.Value)
                .NotEmpty().WithMessage("Write your version, or leave the suggestion as it is.")
                .MaximumLength(AiPolicy.ChangeValueMaxLength);
        }).When(model => model.Edits is not null);

        RuleFor(model => model.Edits!)
            .Must(edits => edits.Select(edit => (edit.ChangeId, edit.Field)).Distinct().Count() == edits.Count)
            .WithMessage("The same field is rewritten more than once.")
            .When(model => model.Edits is not null);

        RuleFor(model => model.Comment).MaximumLength(AiPolicy.MessageMaxLength);
    }
}

/// <summary>What accepting a draft did.</summary>
/// <param name="AiProposalRequestId">The request the draft belongs to — the id the route named.</param>
/// <param name="Status">
/// Where the operation ended up: <c>Accepted</c>, <c>PartiallyAccepted</c> or <c>Rejected</c>. All three are
/// terminal, which is why there is nothing to poll afterwards.
/// </param>
/// <param name="RecipeId">
/// The recipe this created, or <c>null</c> for a rejection.
/// </param>
/// <param name="RecipeVersionNumber">Always <c>1</c> when a recipe was created. Null for a rejection.</param>
/// <param name="AcceptedChangeCount">How many parts of the draft were taken.</param>
/// <param name="RejectedChangeCount">How many were declined. Recorded rather than discarded.</param>
/// <param name="RewrittenChangeCount">
/// How many of the accepted parts the creator rewrote before accepting. Their words went into the recipe;
/// the model's did not. Counted rather than expressed as a disposition, because the part <em>was</em> taken —
/// see <c>AiDraftComposition</c> for why this is the opposite call from the one the proposal panel makes.
/// </param>
/// <param name="DroppedChangeCount">
/// Accepted parts that produced nothing: equipment and a serving size the recipe create contract has nowhere
/// to put (see <c>ProposedRecipeDraft</c>), a field nothing recognises, a value that would not parse, or a
/// row whose group the creator declined. Reported rather than silently lost — a creator who ticked their
/// equipment list is entitled to know it did not arrive.
/// </param>
/// <param name="Replayed">
/// Whether this answered a request that had already been decided. The recipe named is the one that decision
/// created, not a second one.
/// </param>
public sealed record AiDraftAcceptanceServiceModel(
    Guid AiProposalRequestId,
    AiOperationStatus Status,
    Guid? RecipeId,
    int? RecipeVersionNumber,
    int AcceptedChangeCount,
    int RejectedChangeCount,
    int RewrittenChangeCount,
    int DroppedChangeCount,
    bool Replayed,
    DateTimeOffset DecidedAt);

/// <summary>Stable error codes this seam introduces. Renaming one is a breaking API change.</summary>
public static class AiDraftAcceptanceErrors
{
    /// <summary>The request failed shape validation.</summary>
    public const string RequestInvalid = "ai.recipeDraftAcceptance.invalid_request";

    /// <summary>
    /// The selection named a change this draft does not contain, an accept-all did not name every change, or
    /// a rewrite addressed something that is not a field of the row it named. Nothing is written.
    /// </summary>
    public const string SelectionInvalid = "ai.recipeDraftSelection.invalid_request";

    /// <summary>The request exists but has produced no draft to decide about — still queued, or it failed.</summary>
    public const string DraftNotFound = "ai.recipeDraft.not_found";

    /// <summary>
    /// The draft has already been decided differently, or never reached a state where it could be decided.
    /// Terminal states do not reopen, so this is 409 rather than a validation failure.
    /// </summary>
    public const string DraftDecided = "ai.recipeDraft.conflict";

    /// <summary>The caller belongs to the workspace but their role may not create recipes in it.</summary>
    public const string AcceptanceForbidden = "ai.recipeDraftAcceptance.forbidden";
}
