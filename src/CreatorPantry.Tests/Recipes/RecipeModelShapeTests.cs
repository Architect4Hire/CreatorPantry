using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Asserts the shape of the recipe aggregate in the built EF model, rather than the behaviour of the seven
/// entities that happen to be in it today.
/// </summary>
/// <remarks>
/// Every other test in this folder names an entity. An eighth child added to the aggregate tomorrow —
/// without <see cref="IWorkspaceOwned"/>, without a query filter — would pass all of them, and the first
/// sign of trouble would be one workspace reading another's data. These tests discover the entities from the
/// model instead, in the same spirit as <c>WorkspaceOwnershipConvention</c> discovering them rather than
/// listing them.
/// </remarks>
public sealed class RecipeModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// The module's aggregate roots, and the only entities permitted a direct foreign key to a workspace.
    /// </summary>
    /// <remarks>
    /// <see cref="Recipe"/> owns its interior children. <see cref="RecipeVersion"/> is a root of its own
    /// because it outlives the edits that supersede it and is read independently. <see cref="WorkspaceTag"/>
    /// is a root because a creator's tag vocabulary is listed, renamed and retired without reference to any
    /// recipe. Adding a name here is a claim that something is independently owned — it is the one place
    /// that claim is written down, so it should be argued rather than appended.
    /// </remarks>
    private static readonly Type[] AggregateRoots = [typeof(Recipe), typeof(RecipeVersion), typeof(WorkspaceTag)];

    /// <summary>Every concrete entity class declared in the recipe module's entity namespace.</summary>
    public static TheoryData<string> RecipeEntityNames()
    {
        var names = RecipeEntityTypes().Select(type => type.FullName!).ToList();

        // Without this the theory passes vacuously if the namespace is ever renamed.
        Assert.Equal(11, names.Count);

        return [.. names];
    }

    [Theory]
    [MemberData(nameof(RecipeEntityNames))]
    public void Every_recipe_entity_is_workspace_owned_and_filtered(string entityName)
    {
        var entityType = Model().FindEntityType(entityName);

        Assert.True(entityType is not null, $"{entityName} is not in the model at all");
        Assert.True(
            typeof(IWorkspaceOwned).IsAssignableFrom(entityType!.ClrType),
            $"{entityName} does not implement IWorkspaceOwned, so nothing scopes it to a workspace");

        var workspaceId = entityType.FindProperty(nameof(IWorkspaceOwned.WorkspaceId));
        Assert.True(workspaceId is not null, $"{entityName} has no WorkspaceId property");
        Assert.False(workspaceId!.IsNullable, $"{entityName}.WorkspaceId is nullable");

        Assert.True(
            entityType.GetDeclaredQueryFilters().Count > 0,
            $"{entityName} has no global query filter, so a query against it reads every workspace");
    }

    [Theory]
    [MemberData(nameof(RecipeEntityNames))]
    public void Every_recipe_entity_can_be_reached_workspace_first(string entityName)
    {
        var entityType = Model().FindEntityType(entityName)!;

        // Not every index leads with WorkspaceId — the ones EF generates to support a foreign key into the
        // shared reference catalogues necessarily lead with that reference. What must hold is that some
        // index or key does, so a workspace's own rows are reachable without scanning anyone else's.
        var workspaceFirst = entityType.GetIndexes().Select(index => index.Properties)
            .Concat(entityType.GetKeys().Select(key => key.Properties))
            .Any(properties => properties[0].Name == nameof(IWorkspaceOwned.WorkspaceId));

        Assert.True(workspaceFirst, $"{entityName} has no workspace-leading index or key");
    }

    [Fact]
    public void Only_aggregate_roots_point_at_the_workspace()
    {
        var model = Model();

        // An interior child with its own foreign key to Workspaces would give SQL Server a second cascade
        // path to the same rows — and, more to the point, would mean a child's workspace no longer has to
        // agree with its parent's, which is the whole reason WorkspaceId sits inside the composite keys.
        var pointingAtWorkspaces = RecipeEntityTypes()
            .Select(type => model.FindEntityType(type)!)
            .Where(entityType => entityType.GetForeignKeys()
                .Any(key => key.PrincipalEntityType.ClrType == typeof(Workspace)))
            .Select(entityType => entityType.ClrType)
            .ToList();

        Assert.Equal([.. AggregateRoots.OrderBy(type => type.Name)], [.. pointingAtWorkspaces.OrderBy(type => type.Name)]);
    }

    [Fact]
    public void Every_interior_entity_cascades_from_exactly_one_parent_inside_the_module()
    {
        var model = Model();

        foreach (var type in RecipeEntityTypes().Except(AggregateRoots))
        {
            var cascading = model.FindEntityType(type)!.GetForeignKeys()
                .Where(key => key.DeleteBehavior == DeleteBehavior.Cascade)
                .ToList();

            Assert.True(
                cascading.Count == 1,
                $"{type.Name} has {cascading.Count} cascading foreign keys; exactly one keeps the delete path a tree");

            // And that one parent is inside the module, never a shared reference catalogue.
            Assert.Contains(cascading[0].PrincipalEntityType.ClrType, RecipeEntityTypes());
        }
    }

    [Fact]
    public void A_recipe_delete_cannot_reach_its_versions()
    {
        var versions = Model().FindEntityType(typeof(RecipeVersion))!;

        // The constraint the whole "immutable history" claim rests on. If this ever became Cascade, deleting
        // a recipe would silently destroy every version of it and no other test here would notice.
        var toRecipe = versions.GetForeignKeys()
            .Single(key => key.PrincipalEntityType.ClrType == typeof(Recipe));

        Assert.Equal(DeleteBehavior.Restrict, toRecipe.DeleteBehavior);
    }

    private static List<Type> RecipeEntityTypes() => [.. typeof(Recipe).Assembly.GetTypes()
        .Where(type => type.Namespace == typeof(Recipe).Namespace && type is { IsClass: true, IsAbstract: false })
        .OrderBy(type => type.Name)];

    private IModel Model()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model;
    }
}
