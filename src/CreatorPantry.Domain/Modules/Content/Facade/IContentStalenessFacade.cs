using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The content module's answer to a recipe gaining a version, and the read that says whether accepted content
/// is still current.
/// </summary>
/// <remarks>
/// <see cref="IRecipeChangeConsumer"/> is implemented here, so the Recipes module's outbox handler reaches this
/// module through its facade like any other caller (backend.md). The workspace is already resolved by the
/// handler, as the service identity; nothing here resolves one or reads one from the event.
/// </remarks>
public interface IContentStalenessFacade : IRecipeChangeConsumer
{
    /// <summary>
    /// Whether the proposal's accepted content still describes its recipe, decided from its pins rather than
    /// its status (<see cref="ContentCurrency"/>). Null when the proposal is not visible to this workspace.
    /// </summary>
    Task<ContentCurrencyServiceModel?> GetCurrencyAsync(Guid proposalId, CancellationToken cancellationToken);
}

internal sealed class ContentStalenessFacade(
    IContentStalenessBusiness business,
    IThemedDayHistoryInvalidationFacade history) : IContentStalenessFacade
{
    public async Task OnRecipeVersionChangedAsync(RecipeVersionChangedEvent change, CancellationToken cancellationToken)
    {
        await business.ApplyRecipeChangeAsync(change.RecipeId, cancellationToken);

        // A recipe change can mark accepted posts for review, which a themed day's history reports. Retired
        // whether or not this change marked any: the sweep does not say, and a spare invalidation costs one
        // re-derivation.
        await history.InvalidateAsync(cancellationToken);
    }

    public Task<ContentCurrencyServiceModel?> GetCurrencyAsync(Guid proposalId, CancellationToken cancellationToken) =>
        business.GetCurrencyAsync(proposalId, cancellationToken);
}
