using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The one place a themed day's history is cached, and the one place it is invalidated.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every key is built by <see cref="CacheKeys.Workspace"/> from the resolved workspace</strong>, never
/// from a route value or a caller's argument, so every key starts <c>workspace:{id}:</c> and an entry written
/// for one workspace cannot be read from under another (tenancy.md). There is no method here that takes a
/// workspace id.
/// </para>
/// <para>
/// <strong>Invalidation is one stamp per workspace, not one removal per theme.</strong> Each entry's key carries
/// the workspace's current generation; <see cref="InvalidateAsync"/> replaces the generation, which orphans
/// every history entry of that workspace at once and leaves them to expire. A writer therefore does not need to
/// know which theme its change touches — and several cannot: a post is accepted against a piece of work, and
/// only a read can say which theme that work carries. It also closes the race a removal leaves open: a read
/// that queried before a write committed stores its result under the generation it began with, which the write
/// has already retired.
/// </para>
/// </remarks>
public interface IThemedDayHistoryCache
{
    /// <summary>
    /// The cached history for this theme key under the workspace's current generation, or null, together with
    /// the generation the lookup used — which a caller hands back to <see cref="SetAsync"/> so a value derived
    /// before an invalidation cannot be stored as if it came after one.
    /// </summary>
    Task<(ThemedDayHistoryServiceModel? History, string Generation)> GetAsync(
        string themeKey, CancellationToken cancellationToken);

    Task SetAsync(
        string themeKey, string generation, ThemedDayHistoryServiceModel history, CancellationToken cancellationToken);

    /// <summary>Retires every cached history of the resolved workspace.</summary>
    Task InvalidateAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IThemedDayHistoryCache"/>
internal sealed class ThemedDayHistoryCache(IApplicationCache cache, IWorkspaceContext workspace) : IThemedDayHistoryCache
{
    /// <summary>What a workspace that has never invalidated reads under.</summary>
    private const string InitialGeneration = "0";

    public async Task<(ThemedDayHistoryServiceModel? History, string Generation)> GetAsync(
        string themeKey, CancellationToken cancellationToken)
    {
        var generation = (await cache.GetAsync<Stamp>(GenerationKey(), cancellationToken))?.Value
            ?? InitialGeneration;

        var history = await cache.GetAsync<ThemedDayHistoryServiceModel>(
            EntryKey(generation, themeKey), cancellationToken);

        return (history, generation);
    }

    public Task SetAsync(
        string themeKey, string generation, ThemedDayHistoryServiceModel history, CancellationToken cancellationToken) =>
        cache.SetAsync(
            EntryKey(generation, themeKey), history, ThemedDayHistoryPolicy.CacheLifetime, cancellationToken);

    public Task InvalidateAsync(CancellationToken cancellationToken) =>
        cache.SetAsync(
            GenerationKey(),
            new Stamp(Guid.NewGuid().ToString("N")),
            ThemedDayHistoryPolicy.GenerationLifetime,
            cancellationToken);

    private string GenerationKey() =>
        CacheKeys.Workspace(workspace.WorkspaceId, ThemedDayHistoryPolicy.CacheCategory, "generation");

    private string EntryKey(string generation, string themeKey) =>
        CacheKeys.Workspace(
            workspace.WorkspaceId,
            ThemedDayHistoryPolicy.CacheCategory,
            $"{ThemedDayHistoryPolicy.CacheSchemaVersion}:{generation}:{themeKey}");

    /// <summary>The generation, wrapped because the cache stores reference types.</summary>
    private sealed record Stamp(string Value);
}
