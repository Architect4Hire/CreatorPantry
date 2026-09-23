using CreatorPantry.Domain.Managers.Reference;
using System.ComponentModel;
using CreatorPantry.Domain.Managers.Paging;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Vocabulary.Managers;

/// <summary>
/// The query every controlled-vocabulary list accepts. Bound from the query string.
/// </summary>
/// <remarks>
/// <para>
/// There is no workspace parameter, and there must not be. These vocabularies are the shared platform zone
/// (tenancy.md): they have no <c>WorkspaceId</c> to filter on, and their routes are not under
/// <c>/workspaces/{slug}</c>, so no workspace is resolved while they run.
/// </para>
/// <para>
/// One view model serves all seven resources because they take the same filters. The resource itself is not a
/// parameter — it is the route, and therefore also the cursor's scope.
/// </para>
/// </remarks>
public sealed record ReferenceQueryViewModel(
    [property: FromQuery(Name = "search")]
    [property: Description("Free text matched against names and aliases. Terms shorter than two characters are ignored.")]
    string? Search = null,
    [property: FromQuery(Name = "cursor")] string? Cursor = null,
    [property: FromQuery(Name = "limit")] int? Limit = null);

/// <summary>A vocabulary list query after translation: search normalized, cursor decoded and scope-checked, page size clamped.</summary>
/// <param name="Scope">
/// Identifies the ordered set these filters select. Because seven resources share this type, the scope is
/// what stops a cuisine cursor resuming a list of allergens.
/// </param>
public sealed record ReferenceQuery(
    ReferenceSearch? Search,
    ReferenceCursor? Cursor,
    int Limit,
    string Scope)
{
    /// <summary>Identifies this exact page for caching.</summary>
    public string CacheKeySegment => ReferenceQueryKey.Build(Search, Cursor, Limit);
}

/// <summary>One entry in a plain controlled vocabulary: a cuisine, course, equipment type, or food category.</summary>
public sealed record ReferenceEntryRecord(Guid Id, string Code, string DisplayName) : IReferenceRow
{
    public string SortValue => DisplayName;

    public string TieBreaker => Code;
}

/// <summary>A cooking technique and whether guidance about it must carry an explicit caution.</summary>
public sealed record CookingTechniqueRecord(Guid Id, string Code, string DisplayName, bool RequiresSafetyCaution)
    : IReferenceRow
{
    public string SortValue => DisplayName;

    public string TieBreaker => Code;
}

/// <summary>A dietary profile or an allergen: a vocabulary entry whose description is part of its meaning.</summary>
public sealed record DescribedReferenceEntryRecord(Guid Id, string Code, string DisplayName, string Description)
    : IReferenceRow
{
    public string SortValue => DisplayName;

    public string TieBreaker => Code;
}

/// <summary>
/// One entry in a plain controlled vocabulary.
/// </summary>
/// <remarks>
/// No <c>IsActive</c>, on any model in this file. These lists feed pickers and return active entries only, so
/// the flag would be <c>true</c> in every response it ever appeared in. Resolving a retired entry a recipe
/// already references is a lookup by id, and belongs to the feature that needs one.
/// </remarks>
public sealed record ReferenceEntryServiceModel(Guid Id, string Code, string DisplayName);

/// <summary>A cooking method.</summary>
/// <param name="RequiresSafetyCaution">
/// <c>true</c> means guidance about this technique must carry an explicit caution. <c>false</c> means no
/// caution has been attached — it is <em>not</em> a statement that the technique is safe, and no client may
/// render it as one.
/// </param>
public sealed record CookingTechniqueServiceModel(
    Guid Id,
    string Code,
    string DisplayName,
    bool RequiresSafetyCaution);

/// <summary>
/// A dietary pattern, or an allergen. Both carry a required description, and for the same reason: what
/// <c>tree-nuts</c> or <c>gluten-free</c> covers is what decides the meaning of everything recorded against
/// it, so a client that shows the name without the description is showing half the fact.
/// </summary>
public sealed record DescribedReferenceEntryServiceModel(
    Guid Id,
    string Code,
    string DisplayName,
    string Description);
