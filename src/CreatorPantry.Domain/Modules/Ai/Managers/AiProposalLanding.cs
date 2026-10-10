using System.Globalization;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a task does with its stored proposal once it exists, for the tasks that have something to do.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why there is a step after the proposal at all.</strong> A handler runs before the proposal is
/// stored, so it cannot write anything that names one: the id does not exist yet, and a row pointing at a
/// proposal whose write then lost its lease would name nothing. Most tasks need nothing here — their answer
/// <em>is</em> the proposal, and a creator decides about it in place. A task whose answer belongs to another
/// module as records of its own (AF.6.4's posts) lands it here, immediately after the commit, through that
/// module's facade.
/// </para>
/// <para>
/// <strong>Every landing is idempotent.</strong> The worker calls it once after the commit and the maintenance
/// sweep calls it again for a short window afterwards, so a process that died in the gap between the two
/// writes heals itself. Landing twice must therefore be indistinguishable from landing once — which is the
/// receiving module's invariant to hold, not a promise made here.
/// </para>
/// </remarks>
internal interface IAiProposalLandingHandler
{
    /// <summary>Lands the proposal of <paramref name="operationId"/>, or does nothing if it already has.</summary>
    /// <remarks>
    /// The workspace is the caller's resolved one and is never an argument: the worker resolves and validates
    /// it from the operation's own requesting membership before this runs.
    /// </remarks>
    Task LandAsync(Guid operationId, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a stored <see cref="AiTaskType.ChannelPosts"/> proposal into one post revision per channel (AF.6.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It adds nothing and measures nothing.</strong> Every value it writes was computed by the handler and
/// stored on the proposal: the body as written, the count and limit the channel's profile measured, the profile
/// version that measured them, the template provenance, and the recipe pin the context package was read at. A
/// body over its channel's limit lands over its limit — flagged by its stored limit result, never trimmed.
/// </para>
/// <para>
/// <strong>Facade to facade.</strong> The revisions are written through <see cref="ISocialPackageFacade"/>, so
/// the content module's own rules apply in full: the workspace comes from the resolved context, a pin the
/// context does not name is refused, and a second landing of the same proposal writes nothing.
/// </para>
/// <para>
/// <strong>A refusal is logged, not thrown.</strong> The proposal is stored and the creator can read it; what a
/// refused landing costs them is the package, and the remedy is to regenerate. Throwing would report a settled,
/// paid-for run as a worker crash.
/// </para>
/// </remarks>
internal sealed class ChannelPostsProposalLanding(
    IAiOperationDataLayer operations,
    ISocialPackageFacade posts,
    ILogger<ChannelPostsProposalLanding> logger) : IAiProposalLandingHandler
{
    public async Task LandAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var proposal = await operations.FindProposalForLandingAsync(operationId, cancellationToken);

        // No proposal and no creative-context provenance are both "nothing to land": a failed run, or a
        // proposal from some other task. Neither is this landing's business.
        if (proposal?.CreativeContext is not { } context)
        {
            return;
        }

        foreach (var post in Read(proposal))
        {
            var recorded = await posts.RecordGeneratedAsync(
                new SocialGeneratedRevisionInput(
                    context.CreativeContextId,
                    post.ChannelKey,
                    post.Body,
                    post.Limit,
                    proposal.Id,
                    proposal.PromptTemplateId,
                    proposal.PromptTemplateVersion,
                    proposal.PromptTemplateBodyChecksum,
                    context.RecipeId,
                    context.RecipeVersionId,

                    // The brand profile revision is recorded on the proposal as a number rather than as the id
                    // of the revision row, and this column is a foreign key to that row — so the pin that can
                    // be made is the guide version's. Which profile revision wrote a post is answerable from
                    // the proposal the revision names.
                    BrandProfileRevisionId: null,
                    proposal.BrandContext?.BrandGuideVersionId,
                    context.ContextVersion,
                    context.Checksum),
                cancellationToken);

            if (!recorded.Succeeded)
            {
                // The channel key is an identifier and the code is the server's own; no word of the post is
                // logged (ai.md).
                logger.LogError(
                    "The {ChannelKey} post of AI operation {OperationId} could not be stored: {Code}.",
                    post.ChannelKey,
                    operationId,
                    recorded.Error!.Code);
            }
        }
    }

    /// <summary>One post per <see cref="AiChangeTargetKind.ChannelPost"/> target, in the order proposed.</summary>
    /// <remarks>
    /// A target whose rows do not describe one complete post is skipped rather than guessed at: the handler
    /// writes the body, the channel key and the measurement together, so a target missing any of them is not a
    /// post this landing can make true.
    /// </remarks>
    private static IEnumerable<LandedPost> Read(AiProposal proposal)
    {
        var targets = proposal.Changes
            .Where(change => change.TargetKind is AiChangeTargetKind.ChannelPost && change.TargetId is not null)
            .GroupBy(change => change.TargetId!.Value)
            .Select(rows => new
            {
                Body = rows.FirstOrDefault(row => row.ChangeKind is AiChangeKind.Add),
                Fields = rows
                    .Where(row => row.ChangeKind is AiChangeKind.Set && row.FieldName is not null)
                    .ToDictionary(row => row.FieldName!, row => row.AfterValue, StringComparer.Ordinal),
            })
            .Where(target => target.Body is not null)
            .OrderBy(target => target.Body!.ProposedPosition ?? int.MaxValue)
            .ThenBy(target => target.Body!.SortOrder);

        foreach (var target in targets)
        {
            if (target.Fields.GetValueOrDefault(ChannelPostFields.ChannelKey) is not { } channelKey
                || string.IsNullOrWhiteSpace(channelKey)
                || target.Body!.AfterValue is not { } body)
            {
                continue;
            }

            yield return new LandedPost(channelKey, body, Measured(target.Fields));
        }
    }

    /// <summary>
    /// The channel profile's verdict as the proposal recorded it, or null when it recorded none.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess. A revision with no limit result reads as <c>NotChecked</c>, which is true, and
    /// is better than a count this step calculated itself — a second measurement could disagree with the one
    /// the creator was shown beside the generated body.
    /// </remarks>
    private static SocialLimitResult? Measured(IReadOnlyDictionary<string, string?> fields)
    {
        if (fields.GetValueOrDefault(ChannelPostFields.ProfileVersion) is not { } profileVersion
            || string.IsNullOrWhiteSpace(profileVersion)
            || !TryNumber(fields, ChannelPostFields.CharacterCount, out var count)
            || !TryNumber(fields, ChannelPostFields.CharacterLimit, out var limit))
        {
            return null;
        }

        var over = fields.GetValueOrDefault(ChannelPostFields.LimitStatus) == ChannelPostFields.Over;

        return new SocialLimitResult(
            count, limit, over ? SocialLimitStatus.Over : SocialLimitStatus.Within, profileVersion);
    }

    private static bool TryNumber(IReadOnlyDictionary<string, string?> fields, string name, out int value) =>
        int.TryParse(fields.GetValueOrDefault(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private sealed record LandedPost(string ChannelKey, string Body, SocialLimitResult? Limit);
}
