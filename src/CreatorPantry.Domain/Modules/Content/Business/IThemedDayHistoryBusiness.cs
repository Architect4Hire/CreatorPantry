using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>
/// Derives what a workspace has already made for one themed day.
/// </summary>
/// <remarks>
/// <strong>Derived, never authored.</strong> Nothing here is stored and nothing here can be edited: every item
/// is read back from a record of something the creator did — a picture they kept, a post they accepted, a
/// draft they accepted — so the history cannot be made to say something that did not happen.
/// </remarks>
public interface IThemedDayHistoryBusiness
{
    /// <summary>
    /// A theme's whole bounded history, newest outcome first, or
    /// <see cref="ContentErrorCodes.WeeklyThemeNotFound"/> when this workspace has no theme with that key.
    /// </summary>
    /// <remarks>
    /// A retired theme is a theme, so its history reads as before. A deleted one is not, and neither is another
    /// workspace's: all three "no such theme" cases are the one answer the theme lookup gives.
    /// </remarks>
    Task<OperationResult<ThemedDayHistoryServiceModel>> DeriveAsync(string themeKey, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IThemedDayHistoryBusiness"/>
internal sealed class ThemedDayHistoryBusiness(
    IThemedDayHistoryDataLayer dataLayer,
    IWorkspaceWeeklyThemeFacade themes,
    IAiProposalLookupFacade aiLookup,
    IClock clock) : IThemedDayHistoryBusiness
{
    public async Task<OperationResult<ThemedDayHistoryServiceModel>> DeriveAsync(
        string themeKey, CancellationToken cancellationToken)
    {
        // The theme's own lookup decides whether there is a theme, retired or not, and normalizes the key. The
        // stored key is the one read by from here on, never the caller's spelling of it.
        var theme = await themes.FindAsync(themeKey, cancellationToken);

        if (!theme.Succeeded)
        {
            return OperationResult<ThemedDayHistoryServiceModel>.Failure(theme.Error!);
        }

        var sources = await dataLayer.ReadAsync(
            theme.Value!.Key, ThemedDayHistoryPolicy.MaxCandidates, cancellationToken);

        if (sources.Candidates.Count == 0)
        {
            return OperationResult<ThemedDayHistoryServiceModel>.Success(new ThemedDayHistoryServiceModel([]));
        }

        // Facade to facade: whether a recipe began as an accepted draft is the AI module's fact. Asked once for
        // every recipe these pieces of work name, and a recipe written by hand is simply absent from the answer.
        var acceptedDrafts = (await aiLookup.ListAcceptedDraftsAsync(
                [.. sources.RecipeReferences.Select(reference => reference.RecipeId).Distinct()],
                cancellationToken))

            // One recipe is created by one acceptance, so this is a guard against a duplicate rather than a
            // choice between two.
            .GroupBy(draft => draft.RecipeId)
            .ToDictionary(group => group.Key, group => group.Min(draft => draft.AcceptedAt));

        var outcomes = new Dictionary<Guid, List<ThemedDayOutcomeServiceModel>>();

        void Record(Guid contextId, ThemedDayOutcomeServiceModel outcome)
        {
            if (!outcomes.TryGetValue(contextId, out var recorded))
            {
                outcomes[contextId] = recorded = [];
            }

            recorded.Add(outcome);
        }

        foreach (var picture in sources.KeptPictures)
        {
            Record(picture.ContextId, new ThemedDayOutcomeServiceModel(
                ThemedDayOutcomeKind.KeptPicture,
                picture.AddedAt,
                MediaAssetId: picture.MediaAssetId,
                MediaAssetVersionNumber: picture.MediaAssetVersionNumber));
        }

        // One outcome per piece of work however many channels it was accepted for: "the posts were accepted" is
        // the thing that happened, and the channels are its detail.
        foreach (var package in sources.AcceptedChannels.GroupBy(channel => channel.ContextId))
        {
            Record(package.Key, new ThemedDayOutcomeServiceModel(
                ThemedDayOutcomeKind.AcceptedPosts,
                package.Max(channel => channel.AcceptedAt),
                ChannelKeys: [.. package.Select(channel => channel.ChannelKey).Order(StringComparer.Ordinal)],
                NeedsReview: package.Any(channel => channel.NeedsReview)));
        }

        foreach (var reference in sources.RecipeReferences.Distinct())
        {
            if (acceptedDrafts.TryGetValue(reference.RecipeId, out var acceptedAt))
            {
                Record(reference.ContextId, new ThemedDayOutcomeServiceModel(
                    ThemedDayOutcomeKind.AcceptedRecipeDraft, acceptedAt, RecipeId: reference.RecipeId));
            }
        }

        var cutoff = clock.UtcNow.AddDays(-ThemedDayHistoryPolicy.MaxAgeDays);

        var items = new List<ThemedDayHistoryItemServiceModel>();

        foreach (var candidate in sources.Candidates)
        {
            if (!outcomes.TryGetValue(candidate.ContextId, out var recorded))
            {
                continue;
            }

            // Bounded by age per outcome, not per piece of work: work with one recent outcome is listed with
            // that outcome, and work whose every outcome is older than the window is not recent history.
            var recent = recorded
                .Where(outcome => outcome.At >= cutoff)
                .OrderByDescending(outcome => outcome.At)
                .ThenBy(outcome => outcome.Kind)
                .ToList();

            if (recent.Count == 0)
            {
                continue;
            }

            items.Add(new ThemedDayHistoryItemServiceModel(
                candidate.ContextId,
                candidate.WorkingTitle,
                candidate.Day,
                candidate.IsArchived,
                recent[0].At,
                recent));
        }

        // Ordered here rather than in SQL because the ordering value is the newest of three kinds of record, one
        // of which another module holds. The set is capped at MaxCandidates rows before this point.
        items.Sort((left, right) =>
        {
            var byTime = right.LatestOutcomeAt.CompareTo(left.LatestOutcomeAt);

            return byTime != 0 ? byTime : ThemedDayHistoryPosition.CompareIds(right.ContextId, left.ContextId);
        });

        return OperationResult<ThemedDayHistoryServiceModel>.Success(
            new ThemedDayHistoryServiceModel([.. items.Take(ThemedDayHistoryPolicy.MaxItems)]));
    }
}
