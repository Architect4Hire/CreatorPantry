using System.ComponentModel;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a client sends to ask for posts (AF.6.4): one piece of creative work, and the channels to write for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two fields, and the contract is mostly what is absent.</strong> There is no prompt, no template, no
/// model, no provider parameter, no schema version, no scope and no workspace — the workspace is the route's
/// and everything else is the server's. Nothing here can carry a body either: the model writes the copy, and a
/// client that could supply one would be writing a post the provenance would then call generated.
/// </para>
/// <para>
/// <strong>The order of <see cref="ChannelKeys"/> is part of the request.</strong> Posts come back in the
/// order they were named, and the stored inputs are compared verbatim when an idempotency key is replayed —
/// so the same key with the same channels in a different order is a different request, not a retry.
/// </para>
/// </remarks>
public sealed class RequestChannelPostsViewModel
{
    /// <summary>The piece of work the posts are about.</summary>
    [Description("The creative context the posts are about.")]
    public Guid CreativeContextId { get; set; }

    /// <summary>The channels to write for, in the order they should come back.</summary>
    [Description("The channels to write for. One to eight known channel keys, no duplicates.")]
    public IReadOnlyList<string>? ChannelKeys { get; set; }
}

/// <summary>
/// Shape validation for an AF.6.4 request.
/// </summary>
/// <remarks>
/// <para>
/// Takes both channel catalogues, as the brand and photography validators take the first one: a key that names
/// no channel, or one with no writing profile, is a shape failure rather than a lookup — nothing could be
/// written for it and nothing could measure what was.
/// </para>
/// <para>
/// <strong>A retired key is not refused here.</strong> A retired channel keeps its profile, which is what lets
/// a post already written for one be regenerated; whether a <em>new</em> post may be started for it is the
/// content module's rule and the request business asks it, so this validator refusing retired keys would make
/// regeneration impossible.
/// </para>
/// </remarks>
public sealed class RequestChannelPostsViewModelValidator : AbstractValidator<RequestChannelPostsViewModel>
{
    public RequestChannelPostsViewModelValidator(
        IContentChannelCatalog channels, IContentChannelProfileCatalog profiles)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(profiles);

        RuleFor(model => model.CreativeContextId)
            .NotEmpty()
            .WithMessage("Name the piece of work these posts are about.");

        RuleFor(model => model.ChannelKeys)
            .NotNull()
            .Must(keys => keys is { Count: > 0 })
            .WithMessage("Name at least one channel to write for.")
            .Must(keys => keys is null || keys.Count <= AiPolicy.MaxChannelPostsChannels)
            .WithMessage($"Name at most {AiPolicy.MaxChannelPostsChannels} channels.")
            .Must(keys => keys is null || keys.All(key => !string.IsNullOrWhiteSpace(key)
                && key.Trim().Length <= AiPolicy.ChannelPostsChannelKeyMaxLength))
            .WithMessage("Each channel is named by its key.")
            .Must(BeDistinct)
            .WithMessage("Name each channel once.")
            .Must(keys => keys is null || keys.All(key => string.IsNullOrWhiteSpace(key)
                || (channels.Find(key.Trim()) is not null && profiles.Find(key.Trim()) is not null)))
            .WithMessage("That is not a channel CreatorPantry writes posts for.");
    }

    // Ordinal, because a channel key is an exact identifier: "X" is not "x", and treating them as one here
    // would let a request name the same channel twice and be told it had not.
    private static bool BeDistinct(IReadOnlyList<string>? keys) =>
        keys is null
        || keys.Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count() == keys.Count;
}

/// <summary>Stable error codes this seam introduces. Renaming one is a breaking API change.</summary>
public static class AiChannelPostsRequestErrors
{
    public const string RequestInvalid = "ai.channelPosts.invalid_request";

    public const string TaskNotEnabled = "ai.channelPosts.not_enabled";

    /// <summary>
    /// The request names a creative context this workspace does not have.
    /// </summary>
    /// <remarks>
    /// Also the answer for another workspace's context, in the same words, so a post request cannot be used to
    /// ask what a neighbour owns (tenancy.md).
    /// </remarks>
    public const string ContextNotFound = "ai.channelPostsContext.not_found";

    /// <summary>
    /// A named channel is retired and this piece of work has no post for it yet.
    /// </summary>
    /// <remarks>
    /// Well formed but not something the server can accept, so a 422 rather than a 400: the key is a real
    /// channel and the client is not wrong about its spelling. Regenerating a post already written for a
    /// retired channel is unaffected.
    /// </remarks>
    public const string ChannelRetired = "ai.channelPosts.channel.unprocessable";

    public const string RequestNotFound = "ai.channelPostsRequest.not_found";
}

/// <summary>
/// Where a post-writing request has got to, and the posts it produced.
/// </summary>
/// <param name="Request">
/// The operation: its status, and once it has one, the proposal with each channel's body and every server
/// finding about it.
/// </param>
/// <param name="Package">
/// The context's post package, or null when nothing has been written for it yet. The package is the thing a
/// creator edits and decides about, and it outlives any one request — a second request for another channel
/// writes into this same package.
/// </param>
public sealed record ChannelPostRequestStatusServiceModel(
    AiProposalStatusServiceModel Request, SocialPackageServiceModel? Package);
