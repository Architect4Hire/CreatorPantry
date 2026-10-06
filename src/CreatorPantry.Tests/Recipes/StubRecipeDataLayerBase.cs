using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// An <see cref="IRecipeDataLayer"/> whose every member refuses, for a test that exercises one seam of it.
/// </summary>
/// <remarks>
/// <para>
/// A base rather than a hand-written stub per test class, because the interface has fourteen members and a
/// test about the transition command has no business saying anything about thirteen of them. Overriding the
/// one or two a test needs keeps each stub about what it is testing.
/// </para>
/// <para>
/// <strong>It throws rather than returning defaults</strong>, which is the difference between a stub and a
/// trap. A member returning <c>null</c> or an empty page would let a test pass while exercising a path nobody
/// meant it to reach; this way the test fails and names the method.
/// </para>
/// </remarks>
internal abstract class StubRecipeDataLayerBase : IRecipeDataLayer
{
    public virtual Task<IReadOnlyList<RecipeTitleRecord>> ListTitlesAsync(
        IReadOnlyList<Guid> recipeIds, CancellationToken cancellationToken) => throw Unused();

    public virtual Task<CreatedRecipe> CreateAsync(
        Recipe recipe,
        RecipeVersionFacts version,
        IReadOnlyCollection<RecipeTagName> tags,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<TaggedRecipe?> GetDetailAsync(Guid recipeId, CancellationToken cancellationToken) =>
        throw Unused();

    public virtual Task<TaggedRecipe?> GetForUpdateAsync(Guid recipeId, CancellationToken cancellationToken) =>
        throw Unused();

    public virtual Task<RecipeUpdateOutcome> UpdateAsync(
        TaggedRecipe loaded,
        RecipeVersionFacts version,
        IReadOnlyCollection<RecipeTagName>? tags,
        RecipeStatusTransition? reopen,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<(IReadOnlyList<RecipeSummaryRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<(IReadOnlyList<RecipeVersionHistoryRecord> Rows, bool HasMore)?> ListVersionsAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<IReadOnlyList<RecipeVersionSnapshotRecord>?> FindVersionSnapshotsAsync(
        Guid recipeId,
        int firstVersionNumber,
        int secondVersionNumber,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<RecipeRestoreUnit?> GetForRestoreAsync(
        Guid recipeId,
        int versionNumber,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<IReadOnlyList<WorkspaceTag>> FindWorkspaceTagsAsync(
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<RecipeUpdateOutcome> RestoreAsync(
        TaggedRecipe loaded,
        RecipeVersionFacts version,
        IReadOnlyList<WorkspaceTag> tags,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindDuplicateSourceAsync(
        Guid recipeId,
        int? versionNumber,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindCalculationSourceAsync(
        Guid recipeId,
        int versionNumber,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindSnapshotSourceAsync(
        Guid recipeId,
        Guid versionId,
        CancellationToken cancellationToken) => throw Unused();

    public virtual Task<(bool Committed, RecipeVersion? Version)> TryTransitionAsync(
        TaggedRecipe loaded,
        RecipeStatusTransition transition,
        RecipeVersionFacts? version,
        AuditEntry audit,
        CancellationToken cancellationToken) => throw Unused();

    private static NotSupportedException Unused() =>
        new("This test's data layer was asked for something it does not stand in for.");
}
