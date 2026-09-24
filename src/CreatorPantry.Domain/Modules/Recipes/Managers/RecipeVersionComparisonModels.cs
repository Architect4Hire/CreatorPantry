using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The version comparison query: which two of this recipe's versions to compare. Bound from the query string.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace parameter and no recipe parameter.</strong> Both are route segments, resolved
/// server-side before the action runs. A field here for either would be one a request could set (tenancy.md).
/// </para>
/// <para>
/// <strong>Version numbers rather than version ids, and that is a safety decision rather than a taste.</strong>
/// A number is scoped to its recipe by definition — <c>UX_RecipeVersions_Workspace_Recipe_VersionNumber</c>
/// makes it unique within one recipe and nothing else — so the query that resolves it is
/// <c>RecipeId == route id AND VersionNumber == n</c>, and a number naming another recipe's version is not
/// merely refused but unrepresentable. Accepting ids would make "does this version belong to the recipe in the
/// route" a check someone has to remember to write, and forgetting it would expose another recipe's archive to
/// anyone who learned a Guid. Numbers are also what creators cite
/// (<see cref="RecipeVersionHistoryServiceModel.VersionNumber"/>), so the parameter a client sends is the one a
/// creator reads.
/// </para>
/// <para>
/// <strong>Nullable, so that a missing parameter is a refusal rather than a zero.</strong> A non-nullable
/// <c>int</c> would bind an absent <c>from</c> to <c>0</c> and reach the validator as an out-of-range number,
/// which is a different complaint from the true one.
/// </para>
/// <para>
/// Names are given explicitly in lowercase and both parameters carry a <see cref="DescriptionAttribute"/>: the
/// generated OpenAPI document otherwise takes the C# name and, for a parameter with no description of its own,
/// falls back to repeating its endpoint's summary.
/// </para>
/// </remarks>
public sealed record RecipeVersionComparisonViewModel(
    [property: FromQuery(Name = "from")]
    [property: Description("Required. The version number whose values are reported as the 'from' side, as the history lists it.")]
    int? From = null,
    [property: FromQuery(Name = "to")]
    [property: Description("Required. The version number reported as the 'to' side. It may be lower than 'from', which reverses the reading, and it may equal it.")]
    int? To = null);

/// <summary>
/// The version a comparison read, as it describes itself. The content it held is in the comparison, not here.
/// </summary>
/// <remarks>
/// <para>
/// A narrower thing than <see cref="RecipeVersionHistoryServiceModel"/>, deliberately. That model answers "what
/// is this recipe's history" and carries lineage — <c>parentVersionId</c>, <c>aiProposalId</c> — for a screen
/// drawing a chain. This one answers "which two versions am I looking at", and lineage answers nothing about
/// that. Reusing the history model would publish four fields this route has no reading for, and
/// api-contract.md makes removing them later a breaking change.
/// </para>
/// <para>
/// <strong>No snapshot, and no schema version.</strong> A version's document is what the comparison is computed
/// from, not something the route hands back: returning two complete recipes so a client could re-derive the
/// diff is exactly the second, competing diff this route exists to prevent. The schema version the document was
/// written in is a question for the code that reads one.
/// </para>
/// </remarks>
public sealed record RecipeVersionComparisonSideServiceModel
{
    public required Guid VersionId { get; init; }

    /// <summary>The number creators cite. Gap-free and unique within the recipe, and never reused.</summary>
    public required int VersionNumber { get; init; }

    /// <summary>What produced this version. Provenance, not authority.</summary>
    public required RecipeVersionSource Source { get; init; }

    /// <summary>
    /// Whether the creator declared this version finished. Editorial only — never a claim that it was published
    /// anywhere (content.md).
    /// </summary>
    public required RecipeVersionReadiness Readiness { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Two versions of one recipe and everything that differs between them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The comparison is the server's, and it is the only one.</strong> REC-008 wants a diff a creator can
/// approve an edit from, and a client that recomputed one from two snapshots could disagree with the server
/// about what changed — which is why this route returns the answer rather than the inputs. The
/// <see cref="Comparison"/> is <see cref="RecipeComparer"/>'s output verbatim, so the contract and the algebra
/// cannot drift into two descriptions of the same edit.
/// </para>
/// <para>
/// <strong>Read-only, and no cache.</strong> Both versions are immutable, so this answer is stable forever and
/// is the most cacheable read in the module — and it is still not cached, for the reason
/// <see cref="Facade.IRecipeFacade.GetVersionHistoryAsync"/> gives: the first write seam to forget an
/// invalidation is worse than the read it saved. Nothing here writes; comparing two versions leaves no record
/// that it happened.
/// </para>
/// </remarks>
public sealed record RecipeVersionComparisonServiceModel
{
    /// <summary>The version whose values are reported as <c>from</c>. Not necessarily the earlier one.</summary>
    /// <remarks>
    /// The caller chooses the direction, and a creator asking "what would reverting cost me" reads a newer
    /// version as <c>from</c>. The comparison describes the change <em>from</em> this version <em>to</em> the
    /// other, whichever way round their numbers run.
    /// </remarks>
    public required RecipeVersionComparisonSideServiceModel From { get; init; }

    /// <inheritdoc cref="From"/>
    public required RecipeVersionComparisonSideServiceModel To { get; init; }

    public required RecipeComparison Comparison { get; init; }
}

/// <summary>
/// One version's archived content as the repository reads it: enough to name the version, plus the stored
/// document, unparsed.
/// </summary>
/// <remarks>
/// The document travels as text. Turning it into a
/// <see cref="RecipeSnapshotDocument"/> is translation, which belongs to Business — a repository that
/// deserialized would be making a decision about what a stored row means, and would have to decide what to do
/// when it cannot.
/// </remarks>
public sealed record RecipeVersionSnapshotRecord(
    Guid Id,
    int VersionNumber,
    RecipeVersionSource Source,
    RecipeVersionReadiness Readiness,
    DateTimeOffset CreatedAt,
    string Document);
