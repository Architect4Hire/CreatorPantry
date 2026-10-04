using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// One way of shooting a dish — a shot type, not a brand's look. Platform reference data: global, no workspace,
/// and identified by a stable <see cref="Key"/>.
/// </summary>
/// <param name="Key">Lowercase, stable, never reused for a different style.</param>
/// <param name="DisplayName">What a creator sees.</param>
/// <param name="IsActive">
/// False once a style is retired. A retired style stays readable so records that already carry its key keep
/// resolving, and is refused as a new choice, exactly as retired vocabulary entries are.
/// </param>
public sealed record PhotographyStyle(string Key, string DisplayName, bool IsActive = true);

/// <summary>
/// The one source of truth for photography-style keys.
/// </summary>
/// <remarks>
/// <strong>This is craft vocabulary, not brand direction.</strong> How a workspace's own photography looks is
/// the creator's intellectual property and lives in their style guide, as the prose section
/// <c>BrandStyleGuideSectionKey.PhotographyDirection</c> — which is its sole source of truth. These keys name
/// camera-and-light choices a shot can be framed with, the way <c>CookingTechnique</c> names methods a dish can
/// be cooked with. Nothing here describes a brand, and no caller may render a key from this list as a statement
/// about one.
/// </remarks>
public interface IPhotographyStyleCatalog
{
    /// <summary>Every style, active and retired, in display order.</summary>
    IReadOnlyList<PhotographyStyle> All { get; }

    /// <summary>The style with this exact key, or null when no such style has ever existed.</summary>
    PhotographyStyle? Find(string key);
}

/// <summary>
/// The catalogue as configuration in code: versioned with the source, reviewed like source, and needing no
/// migration to change. Not user-managed — a user-managed list would be separate scope.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Provisional list.</strong> The requirement text behind SEED-001 is not in the repository, so these
/// ten are the shot types a food shoot actually chooses between: the angle, the light, and whether the frame is
/// the finished dish, the process, or the table it lands on. Correcting the list is an edit here plus
/// <c>PhotographyStyleCatalogTests</c>.
/// </para>
/// <para>
/// Angle, light and framing are deliberately one flat axis rather than three. A seed offers one idea per facet,
/// and three photography facets would crowd out the other six while describing a shoot nobody asked for in that
/// much detail — "overhead flat-lay" is the suggestion a creator acts on, where "overhead + soft + hero" is a
/// brief. A creator who wants the full brief has their style guide.
/// </para>
/// </remarks>
public sealed class PhotographyStyleCatalog : IPhotographyStyleCatalog
{
    private static readonly PhotographyStyle[] Default =
    [
        new("overhead-flat-lay", "Overhead flat-lay"),
        new("forty-five-degree", "45-degree angle"),
        new("eye-level", "Eye level"),
        new("macro-close-up", "Macro close-up"),
        new("natural-window-light", "Natural window light"),
        new("bright-and-airy", "Bright and airy"),
        new("dark-and-moody", "Dark and moody"),
        new("process-in-action", "Process in action"),
        new("styled-table-scene", "Styled table scene"),
        new("hands-in-frame", "Hands in frame"),
    ];

    private readonly Dictionary<string, PhotographyStyle> _byKey;

    public PhotographyStyleCatalog()
        : this(Default)
    {
    }

    /// <summary>For tests that need a retired style. Keys must be unique.</summary>
    public PhotographyStyleCatalog(IEnumerable<PhotographyStyle> styles)
    {
        All = [.. styles];
        _byKey = All.ToDictionary(style => style.Key, StringComparer.Ordinal);
    }

    public IReadOnlyList<PhotographyStyle> All { get; }

    public PhotographyStyle? Find(string key) => _byKey.GetValueOrDefault(key);
}

public static class PhotographyStyleCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default catalogue unless a host or test has already registered its own. Built with an
    /// explicit factory for the reason <c>AddContentChannelCatalog</c> gives: the second, list-taking constructor
    /// would otherwise win and resolve an empty list.
    /// </summary>
    public static IServiceCollection AddPhotographyStyleCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton<IPhotographyStyleCatalog>(_ => new PhotographyStyleCatalog());

        return services;
    }
}
