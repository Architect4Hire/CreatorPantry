using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>Composes the reads a themed day's history is derived from. Reads only: history is never written.</summary>
public interface IThemedDayHistoryDataLayer
{
    /// <summary>
    /// The most recently touched pieces of work carrying this theme key, with every outcome this module holds
    /// about them.
    /// </summary>
    /// <remarks>
    /// Four bounded reads rather than one query: the candidate set is capped first, and each outcome is then
    /// asked for by those ids alone. A theme with no work costs one query and returns four empty lists.
    /// </remarks>
    Task<ThemedDayHistorySources> ReadAsync(string themeKey, int maxCandidates, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IThemedDayHistoryDataLayer"/>
internal sealed class ThemedDayHistoryDataLayer(IThemedDayHistoryRepository history) : IThemedDayHistoryDataLayer
{
    public async Task<ThemedDayHistorySources> ReadAsync(
        string themeKey, int maxCandidates, CancellationToken cancellationToken)
    {
        var candidates = await history.ListCandidatesAsync(themeKey, maxCandidates, cancellationToken);

        if (candidates.Count == 0)
        {
            return new ThemedDayHistorySources(candidates, [], [], []);
        }

        var contextIds = candidates.Select(candidate => candidate.ContextId).ToList();

        // One after another, not in parallel: they share a DbContext, which serves one query at a time.
        var keptPictures = await history.ListKeptPicturesAsync(contextIds, cancellationToken);
        var acceptedChannels = await history.ListAcceptedChannelsAsync(contextIds, cancellationToken);
        var recipeReferences = await history.ListRecipeReferencesAsync(contextIds, cancellationToken);

        return new ThemedDayHistorySources(candidates, keptPictures, acceptedChannels, recipeReferences);
    }
}
