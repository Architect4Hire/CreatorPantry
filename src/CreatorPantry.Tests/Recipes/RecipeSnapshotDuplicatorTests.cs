using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What a duplicate copies and what it re-identifies, asserted without a database: the duplicator is pure,
/// so every claim here is about object graphs.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing test is <see cref="A_copy_carries_the_same_content_under_new_identities"/>. It states
/// both halves of the contract at once — nothing of the content changes, and nothing of the identity
/// survives — by comparing the source document against a capture of the copy with every id rewritten to its
/// ordinal position. Driven from <c>FullyPopulatedRecipe</c>, so a field added to the aggregate and
/// forgotten in the mapping fails it without anyone extending a list.
/// </para>
/// <para>
/// Tags are the one exception and are checked separately: the duplicator drops them on purpose, because
/// <c>RecipeDataLayer.CreateAsync</c> owns attaching tags to a new recipe. That they end up on the copy is
/// asserted where it becomes true, in <c>RecipeDuplicateEndpointTests</c>.
/// </para>
/// </remarks>
public sealed class RecipeSnapshotDuplicatorTests
{
    private static RecipeSnapshotDocument SourceDocument() =>
        RecipeSnapshotMapper.Capture(
            RecipeAggregateFixture.Complete(RecipeAggregateFixture.FullyPopulatedRecipe()));

    private static RecipeSnapshotDocument Capture(Recipe recipe) =>
        RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe));

    /// <summary>
    /// Every id in a document, in the order they are written, replaced by its position. Two documents
    /// normalized this way are equal exactly when they hold the same content in the same shape — which is
    /// the strongest thing that can be said about a copy whose identities are all supposed to differ.
    /// </summary>
    private static string WithoutIdentity(RecipeSnapshotDocument document)
    {
        var serialized = RecipeSnapshotSerializer.Serialize(document);
        var ordinal = 0;

        foreach (var id in Identities(document))
        {
            serialized = serialized.Replace(
                id.ToString("D"), $"id-{ordinal++}", StringComparison.OrdinalIgnoreCase);
        }

        return serialized;
    }

    /// <summary>Every identity a document carries, in a fixed order.</summary>
    /// <remarks>
    /// Deliberately not the vocabulary and reference ids — cuisine, unit, ingredient, technique, media asset,
    /// tag. Those are references to rows a copy shares with its source rather than identities of its own, and
    /// normalizing them away would hide a duplicator that dropped them.
    /// </remarks>
    private static IEnumerable<Guid> Identities(RecipeSnapshotDocument document)
    {
        foreach (var group in document.IngredientGroups)
        {
            yield return group.Id;

            foreach (var line in group.Ingredients)
            {
                yield return line.Id;
            }
        }

        foreach (var group in document.InstructionGroups)
        {
            yield return group.Id;

            foreach (var step in group.Steps)
            {
                yield return step.Id;
            }
        }

        foreach (var item in document.Equipment)
        {
            yield return item.Id;
        }

        foreach (var link in document.AssetLinks)
        {
            yield return link.Id;
        }
    }

    // ---- The whole contract, in one assertion ----

    [Fact]
    public void A_copy_carries_the_same_content_under_new_identities()
    {
        var source = SourceDocument();

        var copy = RecipeSnapshotDuplicator.Duplicate(source, Guid.NewGuid());

        // Tags are dropped by design, so the comparison is made against a source with none either — every
        // other field must match.
        Assert.Equal(
            WithoutIdentity(source with { Tags = [] }),
            WithoutIdentity(Capture(copy)));
    }

    [Fact]
    public void No_identity_is_shared_with_the_source()
    {
        var source = SourceDocument();

        var copy = Capture(RecipeSnapshotDuplicator.Duplicate(source, Guid.NewGuid()));

        Assert.Empty(Identities(source).Intersect(Identities(copy)));
    }

    /// <summary>
    /// And every copy differs from every other copy, which is what makes duplicating the same recipe twice
    /// produce two recipes rather than one collision.
    /// </summary>
    [Fact]
    public void Two_copies_of_one_source_share_no_identity()
    {
        var source = SourceDocument();

        var first = Capture(RecipeSnapshotDuplicator.Duplicate(source, Guid.NewGuid()));
        var second = Capture(RecipeSnapshotDuplicator.Duplicate(source, Guid.NewGuid()));

        Assert.Empty(Identities(first).Intersect(Identities(second)));
    }

    // ---- Identity and parentage ----

    [Fact]
    public void Every_child_belongs_to_the_new_recipe()
    {
        var recipeId = Guid.NewGuid();

        var copy = RecipeSnapshotDuplicator.Duplicate(SourceDocument(), recipeId);

        Assert.Equal(recipeId, copy.Id);
        Assert.All(copy.IngredientGroups, group => Assert.Equal(recipeId, group.RecipeId));
        Assert.All(copy.IngredientGroups.SelectMany(group => group.Ingredients), line => Assert.Equal(recipeId, line.RecipeId));
        Assert.All(copy.InstructionGroups, group => Assert.Equal(recipeId, group.RecipeId));
        Assert.All(copy.InstructionGroups.SelectMany(group => group.Steps), step => Assert.Equal(recipeId, step.RecipeId));
        Assert.All(copy.Equipment, item => Assert.Equal(recipeId, item.RecipeId));
        Assert.All(copy.AssetLinks, link => Assert.Equal(recipeId, link.RecipeId));
    }

    /// <summary>
    /// A line's group reference points at the copy's group, not at the archived one. Getting this wrong
    /// produces an aggregate whose children point outside it — a foreign key violation at best, and at worst
    /// children attached to the source recipe's groups.
    /// </summary>
    [Fact]
    public void Children_point_at_their_own_new_parents()
    {
        var copy = RecipeSnapshotDuplicator.Duplicate(SourceDocument(), Guid.NewGuid());

        foreach (var group in copy.IngredientGroups)
        {
            Assert.All(group.Ingredients, line => Assert.Equal(group.Id, line.RecipeIngredientGroupId));
        }

        foreach (var group in copy.InstructionGroups)
        {
            Assert.All(group.Steps, step => Assert.Equal(group.Id, step.RecipeInstructionGroupId));
        }
    }

    /// <summary>
    /// Ownership arrives from the resolved context through the interceptor, exactly as on a create. A
    /// duplicator that set a workspace would be one that could place a copy in another.
    /// </summary>
    [Fact]
    public void No_workspace_is_assigned()
    {
        var copy = RecipeSnapshotDuplicator.Duplicate(SourceDocument(), Guid.NewGuid());

        Assert.Equal(Guid.Empty, copy.WorkspaceId);
        Assert.All(copy.IngredientGroups, group => Assert.Equal(Guid.Empty, group.WorkspaceId));
        Assert.All(copy.InstructionGroups, group => Assert.Equal(Guid.Empty, group.WorkspaceId));
        Assert.All(copy.Equipment, item => Assert.Equal(Guid.Empty, item.WorkspaceId));
        Assert.All(copy.AssetLinks, link => Assert.Equal(Guid.Empty, link.WorkspaceId));
    }

    // ---- What is shared rather than copied ----

    /// <summary>
    /// The approved asset policy: the link is new, the asset it names is not. Nothing is copied in storage
    /// and no ownership moves — a link records a usage, and both recipes belong to the one workspace that
    /// owns the asset.
    /// </summary>
    [Fact]
    public void Asset_links_are_new_rows_pointing_at_the_same_assets()
    {
        var source = SourceDocument();

        var copy = RecipeSnapshotDuplicator.Duplicate(source, Guid.NewGuid());

        var original = source.AssetLinks.Single();
        var copied = copy.AssetLinks.Single();

        Assert.NotEqual(original.Id, copied.Id);
        Assert.Equal(original.MediaAssetId, copied.MediaAssetId);
        Assert.Equal(original.Role, copied.Role);
        Assert.Equal(original.Caption, copied.Caption);
        Assert.Equal(original.SortOrder, copied.SortOrder);
    }

    /// <summary>
    /// Normalized references are shared rows, not identities, so they come across untouched — an ingredient
    /// the source had matched is still matched on the copy, and a copy is not silently less well understood
    /// than what it came from.
    /// </summary>
    [Fact]
    public void Reference_ids_are_copied_rather_than_reminted()
    {
        var source = SourceDocument();

        var copy = RecipeSnapshotDuplicator.Duplicate(source, Guid.NewGuid());
        var line = copy.IngredientGroups.Single().Ingredients.Single();
        var archived = source.IngredientGroups.Single().Ingredients.Single();

        Assert.Equal(archived.IngredientId, line.IngredientId);
        Assert.Equal(archived.MeasurementUnitId, line.MeasurementUnitId);
        Assert.Equal(archived.MeasurementUnitDimension, line.MeasurementUnitDimension);
        Assert.Equal(archived.MatchStatus, line.MatchStatus);
        Assert.Equal(source.Recipe.CuisineId, copy.CuisineId);
        Assert.Equal(source.Recipe.YieldUnitId, copy.YieldUnitId);
        Assert.Equal(source.Recipe.YieldUnitDimension, copy.YieldUnitDimension);
    }

    // ---- What the duplicator leaves to its caller ----

    [Fact]
    public void Tag_links_are_left_for_the_create_to_attach()
    {
        var source = SourceDocument();
        Assert.NotEmpty(source.Tags);

        Assert.Empty(RecipeSnapshotDuplicator.Duplicate(source, Guid.NewGuid()).Tags);
    }

    /// <summary>
    /// The archived title and status come back untouched. Deciding that a copy is a Draft called something
    /// new is a domain rule, and Business is where it is applied — asserting it here pins the division.
    /// </summary>
    [Fact]
    public void The_archived_title_and_status_are_left_for_business_to_overrule()
    {
        var source = SourceDocument();

        var copy = RecipeSnapshotDuplicator.Duplicate(source, Guid.NewGuid());

        Assert.Equal(source.Recipe.Title, copy.Title);
        Assert.Equal(source.Recipe.Status, copy.Status);
        Assert.Null(copy.DuplicatedFromVersionId);
    }

    // ---- Documents this build cannot read ----

    [Theory]
    [InlineData(0)]
    [InlineData(RecipeSnapshotDocument.CurrentSchemaVersion + 1)]
    public void An_unreadable_document_is_refused(int schemaVersion) =>
        Assert.Throws<NotSupportedException>(() => RecipeSnapshotDuplicator.Duplicate(
            SourceDocument() with { SchemaVersion = schemaVersion }, Guid.NewGuid()));
}
