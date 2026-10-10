using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a client sends to replace one channel's words with the creator's own (AF.6.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No count, no limit and no status.</strong> The server measures the body against the channel's
/// writing profile and records what it found; a client that could send a verdict would be able to call an
/// over-limit post compliant, and the creator would be reading a measurement nobody made.
/// </para>
/// <para>
/// No workspace, no channel and no context either — those are the route's — and nothing that says where the
/// words came from: an edit is the creator's by definition, which is what distinguishes it in the history from
/// the generated revision it replaces.
/// </para>
/// </remarks>
public sealed class EditChannelPostViewModel
{
    /// <summary>The creator's words for this channel, stored exactly as sent.</summary>
    [Description("Your words for this post.")]
    public string? Body { get; set; }

    /// <summary>
    /// The revision these words were composed against, or null for the first words on a channel.
    /// </summary>
    /// <remarks>
    /// The explicit version check (api-contract.md). An edit composed against anything but the channel's
    /// newest revision is refused rather than stacked on words its author never saw.
    /// </remarks>
    [Description("The revision you were looking at. Omit only when the channel has no post yet.")]
    public Guid? ExpectedLatestRevisionId { get; set; }
}

/// <summary>What a creator decides about one channel's newest post.</summary>
public enum ChannelPostDecision
{
    /// <summary>Not declared. Never valid.</summary>
    Unspecified = 0,

    /// <summary>Take these words. Editor and above.</summary>
    Accept = 1,

    /// <summary>Decline these words. Anything accepted earlier is kept. Editor and above.</summary>
    Reject = 2,

    /// <summary>Write this channel again, leaving every other channel exactly as it is. Contributor and above.</summary>
    Regenerate = 3,

    /// <summary>
    /// Confirm that accepted words whose recipe has changed still stand, as a revision pinned to the recipe as
    /// it is now. Editor and above.
    /// </summary>
    Reaffirm = 4,
}

/// <summary>
/// What a client sends to decide about one channel (AF.6.4).
/// </summary>
/// <remarks>
/// One channel, one decision. Nothing here can reach another channel's slot, which is what makes "regenerating
/// one channel leaves the others untouched" a property of the contract rather than of the implementation.
/// </remarks>
public sealed class ChannelPostDispositionViewModel
{
    /// <summary>The decision.</summary>
    [Description("accept, reject, regenerate, or reaffirm.")]
    public ChannelPostDecision? Decision { get; set; }

    /// <summary>
    /// The revision being decided about. Required to accept or reject; not used by the other two.
    /// </summary>
    /// <remarks>
    /// Named rather than implied, so a decision composed while looking at an older body cannot land on words
    /// its author has not read: the server refuses any id but the channel's newest revision.
    /// </remarks>
    [Description("The revision you are deciding about.")]
    public Guid? RevisionId { get; set; }
}

/// <summary>Shape validation for a creator's edit of one post.</summary>
public sealed class EditChannelPostViewModelValidator : AbstractValidator<EditChannelPostViewModel>
{
    public EditChannelPostViewModelValidator()
    {
        RuleFor(model => model.Body)
            .NotEmpty()
            .WithMessage("A post needs some words.")
            .MaximumLength(ContentPolicy.SocialBodyMaxLength)
            .WithMessage($"A post is at most {ContentPolicy.SocialBodyMaxLength} characters.");
    }
}

/// <summary>Shape validation for a decision about one post.</summary>
public sealed class ChannelPostDispositionViewModelValidator : AbstractValidator<ChannelPostDispositionViewModel>
{
    public ChannelPostDispositionViewModelValidator()
    {
        RuleFor(model => model.Decision)
            .NotNull()
            .Must(decision => decision is ChannelPostDecision.Accept or ChannelPostDecision.Reject
                or ChannelPostDecision.Regenerate or ChannelPostDecision.Reaffirm)
            .WithMessage("Say whether you accept, reject, regenerate or reaffirm this post.");

        // Required for the two decisions that are about particular words, and refused for the two that are
        // not: a regeneration that named a revision would read as a decision about it.
        RuleFor(model => model.RevisionId)
            .Must((model, revisionId) => model.Decision is not (ChannelPostDecision.Accept or ChannelPostDecision.Reject)
                || revisionId is { } id && id != Guid.Empty)
            .WithMessage("Name the revision you are deciding about.");
    }
}
