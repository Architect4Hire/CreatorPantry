using System.ComponentModel;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The body of <c>DELETE /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}</c> (DAM-005).
/// </summary>
/// <remarks>
/// Two fields, and neither is a field of the asset. One says a person meant this; the other says which version of
/// the asset they meant it about.
/// </remarks>
public sealed record DeleteMediaAssetViewModel
{
    /// <summary>
    /// That a person decided this. Required, and must be <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Not redundant beside <see cref="ExpectedConcurrencyToken"/>, which says what the caller believes the asset
    /// looks like rather than that they meant to remove it. Deleting takes a piece of finished work out of every
    /// collaborator's library and can leave another creator's recipe pointing at a tombstone, so an API client has
    /// to state the step a creator took in the interface rather than reach it by sending a well-formed body —
    /// publishing.md's confirmation rule, applied the way
    /// <c>ActivateBrandStyleGuideVersionViewModel</c> applies it.
    /// </remarks>
    [Description("Must be true. States that a person confirmed this deletion; a request without it is refused.")]
    public bool? Confirmed { get; init; }

    /// <summary>
    /// The <c>concurrencyToken</c> from the asset this deletion was decided against. Required.
    /// </summary>
    /// <remarks>
    /// Checked <strong>before</strong> the already-deleted answer, following <c>RecipeBusiness.TransitionAsync</c>
    /// and for the reason it gives: a caller quoting a stale token has not seen what the asset looks like now, and
    /// answering "already deleted" would hide a collaborator's work from them. The consequence to know is that
    /// retrying a deletion whose response was lost needs a fresh read first — the retry is then a no-op that
    /// returns the existing tombstone.
    /// </remarks>
    public string? ExpectedConcurrencyToken { get; init; }
}

/// <summary>
/// What is still pointing at an asset that has just been deleted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every link survives a deletion</strong>, deliberately: cutting a recipe's photograph silently is the one
/// thing DAM-005 must not do, and all three link types carry <c>Restrict</c> foreign keys into
/// <c>MediaAsset</c> so a hard delete was never representable while any existed. This record is how the creator is
/// told what they have just left stale, instead of finding out from a broken page.
/// </para>
/// <para>
/// <strong>Recipes are named; the other two are counted.</strong> A recipe is the thing a creator will go and fix,
/// and <c>IRecipeFacade.ListTitlesAsync</c> already exists to name one (12.9c). A brand profile's asset slots and a
/// test run's attachments are reached from their own screens, and naming them would mean a cross-module lookup per
/// link type for something nobody has asked to see — a count answers "is there anything here" without inventing
/// two more facade methods.
/// </para>
/// <para>
/// A recipe this workspace cannot name is absent from <see cref="Recipes"/> but still counted in
/// <see cref="RecipeCount"/>. As the schema stands the two always agree, for the reason 12.9c records.
/// </para>
/// </remarks>
public sealed record MediaAssetAffectedContentServiceModel(
    int RecipeCount,
    IReadOnlyList<RecipeLinkCandidateServiceModel> Recipes,
    int BrandProfileCount,
    int TestAttachmentCount)
{
    /// <summary>Whether anything at all still references the asset.</summary>
    /// <remarks>
    /// Computed rather than stored so it cannot disagree with the counts beside it. Published because it is the one
    /// thing a client acts on — whether to show a "this is still in use" warning at all.
    /// </remarks>
    public bool Any => RecipeCount > 0 || BrandProfileCount > 0 || TestAttachmentCount > 0;
}

/// <summary>One asset's deletion, as the route reports it (DAM-005).</summary>
/// <remarks>
/// <para>
/// Not the whole detail: a caller who has just removed an asset does not need its version history or its prompt
/// lineage, and the detail read with <c>?includeDeleted=true</c> is there for one who does. What this carries is
/// the tombstone and the impact — the two things the deletion itself established.
/// </para>
/// <para>
/// <strong>No bytes are removed</strong>, here or by any later sweep this prompt defines. The objects stay, which
/// is what makes the operation answer fast and makes a half-done deletion impossible: there is no second system to
/// fail (media.md, and this prompt's restriction).
/// </para>
/// </remarks>
/// <param name="AlreadyDeleted">
/// True when this call found the asset already deleted and changed nothing. The timestamp and actor are then the
/// original ones, and no second audit entry was written.
/// </param>
public sealed record MediaAssetDeletionServiceModel(
    Guid Id,
    string Title,
    DateTimeOffset DeletedAt,
    Guid DeletedByMembershipId,
    bool AlreadyDeleted,
    MediaAssetAffectedContentServiceModel Affected,
    string ConcurrencyToken);
