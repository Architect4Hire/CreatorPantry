using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// An eating situation a recipe can be written for — a weeknight, a party, a lunchbox. Platform reference data:
/// global, no workspace, and identified by a stable <see cref="Key"/>.
/// </summary>
/// <param name="Key">Lowercase, stable, never reused for a different occasion.</param>
/// <param name="DisplayName">What a creator sees.</param>
/// <param name="IsActive">
/// False once an occasion is retired. A retired occasion stays readable so records that already carry its key
/// keep resolving, and is refused as a new choice, exactly as retired vocabulary entries are.
/// </param>
public sealed record Occasion(string Key, string DisplayName, bool IsActive = true);

/// <summary>
/// The one source of truth for occasion keys.
/// </summary>
/// <remarks>
/// An occasion is the situation food is eaten in, which is a different axis from the meal it is
/// (<c>Course</c>) and from the day it publishes on (a workspace's own weekly theme). Breakfast is a course,
/// Meat-free Monday is a creator's theme, and "make-ahead for the week" is an occasion; a seed can carry all
/// three without repeating itself.
/// </remarks>
public interface IOccasionCatalog
{
    /// <summary>Every occasion, active and retired, in display order.</summary>
    IReadOnlyList<Occasion> All { get; }

    /// <summary>The occasion with this exact key, or null when no such occasion has ever existed.</summary>
    Occasion? Find(string key);
}

/// <summary>
/// The catalogue as configuration in code: versioned with the source, reviewed like source, and needing no
/// migration to change. Not user-managed — a user-managed list would be separate scope.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Provisional list.</strong> The requirement text behind SEED-001 is not in the repository, so these
/// twelve are the situations a food creator plans content around across a year. Correcting the list is an edit
/// here plus <c>OccasionCatalogTests</c>.
/// </para>
/// <para>
/// Deliberately season- and situation-shaped rather than named holidays. "Holiday baking" earns a key because
/// it describes how the cooking differs; Thanksgiving and Diwali do not, because a dated calendar of the world's
/// holidays is a different feature with a different lifecycle, and a half-built one that knew four of them would
/// be worse than none. A creator planning a specific holiday says so in their own words.
/// </para>
/// </remarks>
public sealed class OccasionCatalog : IOccasionCatalog
{
    private static readonly Occasion[] Default =
    [
        new("weeknight", "Weeknight"),
        new("weekend-project", "Weekend project"),
        new("make-ahead", "Make-ahead"),
        new("batch-cooking", "Batch cooking"),
        new("one-pan", "One-pan"),
        new("budget", "Budget"),
        new("entertaining", "Entertaining"),
        new("celebration", "Celebration"),
        new("holiday-baking", "Holiday baking"),
        new("picnic-and-outdoors", "Picnic and outdoors"),
        new("lunchbox", "Lunchbox"),
        new("comfort-food", "Comfort food"),
    ];

    private readonly Dictionary<string, Occasion> _byKey;

    public OccasionCatalog()
        : this(Default)
    {
    }

    /// <summary>For tests that need a retired occasion. Keys must be unique.</summary>
    public OccasionCatalog(IEnumerable<Occasion> occasions)
    {
        All = [.. occasions];
        _byKey = All.ToDictionary(occasion => occasion.Key, StringComparer.Ordinal);
    }

    public IReadOnlyList<Occasion> All { get; }

    public Occasion? Find(string key) => _byKey.GetValueOrDefault(key);
}

public static class OccasionCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default catalogue unless a host or test has already registered its own. Built with an
    /// explicit factory for the reason <c>AddContentChannelCatalog</c> gives: the second, list-taking constructor
    /// would otherwise win and resolve an empty list.
    /// </summary>
    public static IServiceCollection AddOccasionCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton<IOccasionCatalog>(_ => new OccasionCatalog());

        return services;
    }
}
