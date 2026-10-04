namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>One selected facet: the stable key a caller can pin next time, and the name to show.</summary>
/// <param name="Pinned">
/// True when the caller asked for this value rather than the seed choosing it. Lets a client show at a glance
/// which parts of an idea are the creator's and which are the generator's.
/// </param>
public sealed record ContentSeedFacetServiceModel(string Key, string DisplayName, bool Pinned);

/// <summary>
/// The method facet, which carries one fact the others do not.
/// </summary>
/// <param name="RequiresSafetyCaution">
/// <c>true</c> means guidance about this technique must carry an explicit caution, and a client showing this seed
/// must show one. <c>false</c> means no caution has been attached — it is <em>not</em> a statement that the
/// technique is safe, and no client may render it as one.
/// </param>
public sealed record ContentSeedMethodServiceModel(
    string Key,
    string DisplayName,
    bool Pinned,
    bool RequiresSafetyCaution);

/// <summary>
/// The day a seed is for, and the workspace's own theme for that day when it has one.
/// </summary>
/// <param name="Theme">
/// Null when the workspace has no live theme on this day, which is the normal case for most of the week and is
/// not an error. A client shows the day alone.
/// </param>
public sealed record ContentSeedDayServiceModel(DayOfWeek Day, bool Pinned, ContentSeedFacetServiceModel? Theme);

/// <summary>
/// One content idea, assembled from reference vocabulary and the workspace's own week.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is persisted and nothing here came from a model. A seed is a suggestion the creator accepts,
/// edits or throws away, and asking again with the same <see cref="Token"/> returns it again.
/// </para>
/// <para>
/// A facet can be absent. An empty or fully retired catalogue, or a workspace with no theme on the chosen day,
/// yields a seed with fewer parts rather than an error — the generator degrades instead of refusing.
/// </para>
/// </remarks>
/// <param name="Token">
/// What produced this seed. Echoed back so a creator can reproduce or share it; see
/// <c>IContentSeedBusiness.GenerateAsync</c> for the limits of that reproducibility.
/// </param>
/// <param name="Description">
/// One line naming the seed's own parts, assembled by <c>ContentSeedDescription</c>. Deterministic and never
/// model-written: it restates the facets below, plus fixed connecting text and — for a method that carries one —
/// a fixed safety caution. It adds no fact that is not already a facet.
/// </param>
/// <remarks>
/// <strong>For whoever first passes a seed to a model.</strong> <paramref name="Description"/> can contain the
/// creator's own words, because a weekly theme's display name is free text they wrote. So it is creator content
/// rather than platform text, and a prompt that includes it must delimit it as untrusted source material like any
/// other creator input (ai.md). Nothing here calls a model today, which is the only reason this is a note and not
/// a rule being broken.
/// </remarks>
public sealed record ContentSeedServiceModel(
    string Token,
    ContentSeedFacetServiceModel? Cuisine,
    ContentSeedFacetServiceModel? DishType,
    ContentSeedMethodServiceModel? Method,
    ContentSeedFacetServiceModel? PhotographyStyle,
    ContentSeedFacetServiceModel? Channel,
    ContentSeedDayServiceModel Day,
    ContentSeedFacetServiceModel? Occasion,
    string Description);
