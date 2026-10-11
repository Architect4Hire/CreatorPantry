using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>
/// The reads behind "what has this workspace made for this themed day". Every one is inside the workspace
/// query filter, and none of them returns creator text beyond a working title.
/// </summary>
public interface IThemedDayHistoryRepository
{
    /// <summary>
    /// The pieces of work carrying this theme key, most recently touched first, archived ones included.
    /// </summary>
    Task<List<ThemedDayCandidate>> ListCandidatesAsync(
        string themeKey, int take, CancellationToken cancellationToken);

    /// <summary>The library pictures these pieces of work mark as their keeper.</summary>
    Task<List<ThemedDayKeptPicture>> ListKeptPicturesAsync(
        IReadOnlyCollection<Guid> contextIds, CancellationToken cancellationToken);

    /// <summary>The channels of these pieces of work that hold an accepted post.</summary>
    Task<List<ThemedDayAcceptedChannel>> ListAcceptedChannelsAsync(
        IReadOnlyCollection<Guid> contextIds, CancellationToken cancellationToken);

    /// <summary>The recipes these pieces of work name.</summary>
    Task<List<ThemedDayRecipeReference>> ListRecipeReferencesAsync(
        IReadOnlyCollection<Guid> contextIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IThemedDayHistoryRepository"/>
internal sealed class ThemedDayHistoryRepository(CreatorPantryDbContext context) : IThemedDayHistoryRepository
{
    public Task<List<ThemedDayCandidate>> ListCandidatesAsync(
        string themeKey, int take, CancellationToken cancellationToken) =>
        context.CreativeContexts.AsNoTracking()

            // With the workspace the filter supplies, the leading edge of
            // IX_CreativeContexts_Workspace_Theme_Updated — a seek that arrives in the order asked for. No
            // ArchivedAt predicate, and the index has none either: archiving does not unmake what came of work.
            .Where(candidate => candidate.WeeklyThemeKey == themeKey)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenByDescending(candidate => candidate.Id)
            .Take(take)
            .Select(candidate => new ThemedDayCandidate(
                candidate.Id, candidate.WorkingTitle, candidate.Day, candidate.ArchivedAt != null))
            .ToListAsync(cancellationToken);

    public Task<List<ThemedDayKeptPicture>> ListKeptPicturesAsync(
        IReadOnlyCollection<Guid> contextIds, CancellationToken cancellationToken) =>
        context.CreativeContextReferences.AsNoTracking()

            // A library asset, not a staged image: a keeper mark on a picture that was never saved is an
            // intention, and a staged image can still be rejected or expire.
            .Where(reference =>
                contextIds.Contains(reference.CreativeContextId)
                && reference.Kind == CreativeContextReferenceKind.DamAsset
                && reference.Purpose == CreativeContextReferencePurpose.Keeper
                && reference.MediaAssetId != null)
            .Select(reference => new ThemedDayKeptPicture(
                reference.CreativeContextId,
                reference.MediaAssetId!.Value,
                reference.MediaAssetVersionNumber,
                reference.AddedAt))
            .ToListAsync(cancellationToken);

    public Task<List<ThemedDayAcceptedChannel>> ListAcceptedChannelsAsync(
        IReadOnlyCollection<Guid> contextIds, CancellationToken cancellationToken) =>
        context.SocialPackageChannels.AsNoTracking()

            // The accepted pointer, whatever the slot is doing now. It is kept through every later move — an
            // edit, a regeneration and a rejection of newer words all leave it where it was — so a channel that
            // has one is a channel the creator accepted a post for, and nothing since has made that untrue.
            .Where(channel => channel.AcceptedRevisionId != null)

            // Both sets are filtered by workspace, so the join cannot reach across one.
            .Join(
                context.SocialPackages.AsNoTracking().Where(package => contextIds.Contains(package.CreativeContextId)),
                channel => channel.SocialPackageId,
                package => package.Id,
                (channel, package) => new ThemedDayAcceptedChannel(
                    package.CreativeContextId,
                    channel.ChannelKey,
                    channel.Status == ContentProposalStatus.NeedsReview,

                    // Present whenever the pointer is, which a check constraint holds. Not UpdatedAt, which
                    // moves with every later draft and would let an edit reorder history.
                    channel.AcceptedAt!.Value))
            .ToListAsync(cancellationToken);

    public Task<List<ThemedDayRecipeReference>> ListRecipeReferencesAsync(
        IReadOnlyCollection<Guid> contextIds, CancellationToken cancellationToken) =>
        context.CreativeContextReferences.AsNoTracking()
            .Where(reference =>
                contextIds.Contains(reference.CreativeContextId)
                && reference.Kind == CreativeContextReferenceKind.Recipe
                && reference.RecipeId != null)
            .Select(reference => new ThemedDayRecipeReference(reference.CreativeContextId, reference.RecipeId!.Value))
            .ToListAsync(cancellationToken);
}
