using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One entry in a recipe's history: what the version says about itself, where it came from, and nothing of
/// what it contains.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The snapshot is absent, and that is the point of this shape.</strong> A recipe's archive is every
/// byte of every version it has ever had; listing the history must read none of it. The document lives in its
/// own table for exactly this reason, and the query behind this model never names that table — see
/// <see cref="Data.IRecipeVersionRepository.ListHistoryAsync"/>. A client that wants a version's content asks
/// for that version.
/// </para>
/// <para>
/// <strong>A separate record from <see cref="RecipeVersionSummaryServiceModel"/></strong>, which is what a
/// recipe read publishes about its current version. The two overlap in six fields and differ in two, and the
/// difference is the reason: <see cref="RecipeDetailServiceModel"/> states that lineage and provenance belong
/// to the history and diff seams rather than to reading the recipe as it stands. Widening that model to serve
/// this route would publish <see cref="ParentVersionId"/> on every recipe read, where it answers nothing.
/// </para>
/// <para>
/// <strong>The author is a name, never an id.</strong> <c>WorkspaceId</c> and the membership columns never
/// leave the server (tenancy.md), so <see cref="CreatedByName"/> is resolved before the entry is published —
/// by the Facade, through the Tenancy facade, because memberships and user display names belong to other
/// modules. It is nullable because a version outlives the membership that wrote it by design.
/// </para>
/// <para>
/// <strong>What else is withheld.</strong> <c>BasedOnRecipeRowVersion</c> is a copy of a concurrency token the
/// recipe has long since moved past: a client may never quote it, and publishing it would put a second thing
/// called a token into a contract that already has one on the detail read. <c>SnapshotSchemaVersion</c> tells a
/// reader whether it understands a stored document, which is a question for the seams that read one.
/// </para>
/// <para>
/// <strong>There is no <c>isCurrent</c>.</strong> The list is newest first, so the current version is the first
/// row of the first page — a fact the ordering already states. Publishing it per row would mean comparing every
/// row against the recipe's highest version number, which is a subquery bought to repeat something the client
/// can read off the shape of the answer.
/// </para>
/// </remarks>
public sealed record RecipeVersionHistoryServiceModel
{
    public required Guid Id { get; init; }

    /// <summary>
    /// The number creators cite. Gap-free and unique within the recipe, and never reused — the same statement
    /// <see cref="RecipeVersionSummaryServiceModel.VersionNumber"/> makes, because a client reading a recipe
    /// and a client reading its history are the same client and must not be told two different things.
    /// </summary>
    public required int VersionNumber { get; init; }

    /// <summary>
    /// What produced this version. Provenance, not authority: a version is not more trustworthy for having
    /// come from a creator's own edit than from an accepted proposal.
    /// </summary>
    public required RecipeVersionSource Source { get; init; }

    /// <summary>
    /// Whether the creator declared this version finished. Editorial only — never a claim that it was
    /// published anywhere (content.md).
    /// </summary>
    public required RecipeVersionReadiness Readiness { get; init; }

    /// <summary>Why this version was written, when the writer gave a reason. A routine save has none.</summary>
    public required string? Reason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The display name of whoever wrote this version, or <c>null</c> when the membership behind it can no
    /// longer be named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name rather than the membership id the row actually stores. An id is not an author — it names
    /// nothing a creator can read, and it is an internal identifier the recipe read deliberately withholds.
    /// The Facade exchanges one for the other through the Tenancy facade, which is the only way across:
    /// memberships belong to that module and display names to Auth beyond it.
    /// </para>
    /// <para>
    /// <strong>Nullable, and it will happen.</strong> Authorship is recorded so that it survives a member
    /// leaving the workspace — the membership column is deliberately not a foreign key for that reason — so a
    /// version can outlive the membership that wrote it. The honest answer then is that the version exists
    /// and its author cannot be named, not that the version is broken.
    /// </para>
    /// </remarks>
    public required string? CreatedByName { get; init; }

    /// <summary>
    /// The version this one was derived from; <c>null</c> only for version 1.
    /// </summary>
    /// <remarks>
    /// Published rather than left to be inferred from <see cref="VersionNumber"/>, because the numbers cannot
    /// express what a restore does: restoring version 2 over version 5 writes version 6, whose content came
    /// from 2 and whose parent is 5. A history screen that drew its lineage from the numbering would draw that
    /// edit as an ordinary edit of 5.
    /// </remarks>
    public required Guid? ParentVersionId { get; init; }

    /// <summary>
    /// The version this one's content was copied from, when <see cref="Source"/> is
    /// <see cref="RecipeVersionSource.Restore"/>; <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// The other half of the sentence <see cref="ParentVersionId"/> starts. Restoring version 2 over version 5
    /// writes version 6 with a parent of 5 and this set to 2, so a history screen can say what was restored
    /// rather than only that something was. Like <see cref="AiProposalId"/>, the pairing with
    /// <see cref="Source"/> is a database check constraint, so a row claiming one without the other cannot
    /// exist to be published.
    /// </remarks>
    public required Guid? RestoredFromVersionId { get; init; }

    /// <summary>
    /// The AI generation whose proposal the creator accepted, when <see cref="Source"/> is
    /// <see cref="RecipeVersionSource.AiProposalAccepted"/>; <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// The pairing is a database check constraint rather than a convention, so a row carrying one without the
    /// other cannot exist to be published. This is the traceability ai.md asks for: a version a model helped
    /// write stays identifiable after the fact, from the history itself rather than from a free-text reason.
    /// </remarks>
    public required Guid? AiProposalId { get; init; }

}

/// <summary>
/// A page of history as Business can build it, and the authorship it cannot resolve on its own.
/// </summary>
/// <remarks>
/// <para>
/// The one place in this module where a ServiceModel leaves Business incomplete, and the reason is
/// structural rather than stylistic. Naming an author means reading a membership and the user behind it —
/// two modules Business may not call, since it calls its own DataLayer and nothing else. The Facade may,
/// and does. So Business builds everything it can and hands over the ids it could not spend.
/// </para>
/// <para>
/// <strong>Keyed by version id, not by position.</strong> The two collections could have been left to line
/// up index by index, and would have, until the day something reordered or filtered one of them; a key that
/// is already unique costs nothing and cannot drift.
/// </para>
/// <para>
/// The membership ids stop at the Facade. <see cref="RecipeVersionHistoryServiceModel.CreatedByName"/>
/// records why they must not travel further.
/// </para>
/// </remarks>
/// <param name="Page">The page, with every entry's <c>CreatedByName</c> still null.</param>
/// <param name="AuthorMembershipByVersionId">The membership that wrote each version on this page.</param>
public sealed record RecipeVersionHistoryPageResult(
    CursorPageServiceModel<RecipeVersionHistoryServiceModel> Page,
    IReadOnlyDictionary<Guid, Guid> AuthorMembershipByVersionId);
