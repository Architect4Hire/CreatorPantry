using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What the creator decided about a proposal as a whole.</summary>
/// <remarks>
/// <see cref="AcceptAll"/> and <see cref="AcceptSelected"/> are separate members although both are answered by
/// the same confirmed list. They record different intents, and the difference is checkable: an
/// <see cref="AcceptAll"/> whose list does not name every change is refused, which is what stops "accept
/// everything" from being sent by a client looking at a different proposal than the one it is dispositioning.
/// </remarks>
public enum AiDispositionDecision
{
    /// <summary>Not declared. Never valid: a disposition that says nothing is not a decision.</summary>
    Unspecified = 0,

    /// <summary>Take every proposed change. The confirmation must name all of them.</summary>
    AcceptAll = 1,

    /// <summary>Take the named changes and decline the rest.</summary>
    AcceptSelected = 2,

    /// <summary>Take none of them. A perfectly good outcome, and it records what was declined.</summary>
    Reject = 3,
}

/// <summary>
/// The body of <c>POST .../ai-proposals/{aiProposalId}/disposition</c>: a creator deciding what to do with
/// generated content they have reviewed.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="AcceptedChangeIds"/> is the explicit confirmation</strong> that publishing.md and
/// AIREC-GR-007 require before generated content replaces anything a creator owns. It is not a convenience for
/// partial acceptance: naming the changes is how the request states what was reviewed, so a client cannot
/// accept content nobody looked at by sending a single flag. A decision with no list, or a list naming a change
/// this proposal does not contain, is refused with nothing written.
/// </para>
/// <para>
/// <strong>There is nothing here that could change what is applied.</strong> No values, no fields, no targets,
/// no recipe, no version, no workspace — only ids of changes the server already computed and stored. A client
/// choosing which of the server's own changes to take cannot smuggle a different change in alongside them.
/// </para>
/// <para>
/// Feedback rides along with the decision rather than having a route of its own. The moment a creator has just
/// read a diff and decided about it is the moment their opinion of it is worth recording, and a proposal reaches
/// a terminal state here, so there is no later moment at which the decision could be revisited anyway.
/// </para>
/// </remarks>
public sealed class AiProposalDispositionViewModel
{
    /// <summary>Accept everything, accept a selection, or reject.</summary>
    public AiDispositionDecision Decision { get; set; }

    /// <summary>
    /// The changes being accepted, named by the <c>changeId</c> the proposal published for each. Required for
    /// both accept decisions, and must be empty for a rejection.
    /// </summary>
    public IReadOnlyList<Guid>? AcceptedChangeIds { get; set; }

    /// <summary>Whether the proposal was useful. Optional, and independent of the decision.</summary>
    /// <remarks>
    /// Independent deliberately: a rejected proposal can be genuinely helpful — it may have shown the creator
    /// something about their own recipe — and an accepted one can be barely adequate. Inferring the answer from
    /// the decision would throw away the only signal here worth having.
    /// </remarks>
    public bool? WasHelpful { get; set; }

    /// <summary>The creator's own words about the proposal. Optional.</summary>
    public string? Comment { get; set; }
}

public sealed class AiProposalDispositionViewModelValidator : AbstractValidator<AiProposalDispositionViewModel>
{
    public AiProposalDispositionViewModelValidator()
    {
        RuleFor(model => model.Decision)
            .NotEqual(AiDispositionDecision.Unspecified)
            .WithMessage("Say whether you are accepting or rejecting this proposal.")
            .IsInEnum().WithMessage("That is not a decision this proposal accepts.");

        When(model => model.Decision is AiDispositionDecision.AcceptAll or AiDispositionDecision.AcceptSelected, () =>
        {
            RuleFor(model => model.AcceptedChangeIds)
                .NotNull().WithMessage("Name the changes you are accepting.")
                .Must(ids => ids is null || ids.Count > 0)
                .WithMessage("Name the changes you are accepting.")
                .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
                .WithMessage("One of the accepted changes has no id.")
                .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
                .WithMessage("The same change is listed more than once.");
        });

        // A rejection that names changes is a client that has not decided what it is asking for. Refusing is
        // safer than picking one of the two readings, because one of them writes to the creator's recipe.
        When(model => model.Decision is AiDispositionDecision.Reject, () =>
        {
            RuleFor(model => model.AcceptedChangeIds)
                .Must(ids => ids is null || ids.Count == 0)
                .WithMessage("A rejection cannot also accept changes.");
        });

        RuleFor(model => model.Comment)
            .MaximumLength(AiPolicy.MessageMaxLength)
            .WithMessage($"Keep the comment to {AiPolicy.MessageMaxLength} characters or fewer.");
    }
}

/// <summary>What a disposition did.</summary>
/// <param name="AiProposalRequestId">The request the proposal belongs to — the id the route named.</param>
/// <param name="Status">
/// Where the operation ended up: <c>Accepted</c>, <c>PartiallyAccepted</c> or <c>Rejected</c>. All three are
/// terminal, which is why there is nothing to poll afterwards.
/// </param>
/// <param name="AcceptedChangeCount">How many changes were taken.</param>
/// <param name="RejectedChangeCount">How many were declined. Recorded rather than discarded.</param>
/// <param name="RecipeVersionNumber">
/// The version <em>this call</em> wrote, or <c>null</c> when it wrote none.
/// </param>
/// <remarks>
/// <para>
/// There are three ways <paramref name="RecipeVersionNumber"/> is null, and a client cannot tell them apart: the
/// proposal was rejected; every accepted change already matched the recipe, so no version was written at all (the
/// same no-op rule a creator's own edit follows); or this was a replay, and the call that wrote the version was
/// the earlier one. Reporting a version this call did not write would be claiming work it did not do, and
/// carrying the earlier call's number would need the recipe module to be asked which version an accepted
/// proposal produced — a read that does not exist. A client that needs the number reads the recipe.
/// </para>
/// </remarks>
public sealed record AiProposalDispositionServiceModel(
    Guid AiProposalRequestId,
    AiOperationStatus Status,
    int AcceptedChangeCount,
    int RejectedChangeCount,
    int? RecipeVersionNumber,
    DateTimeOffset DecidedAt);
