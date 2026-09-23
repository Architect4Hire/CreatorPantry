using System.Reflection;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Makes "add a column to an entity and forget to publish it" a failing test rather than something a reviewer
/// has to notice — the read-seam counterpart to <see cref="RecipeSnapshotCompletenessTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against is quiet in a way the snapshot's is not. A field missing from the archive
/// eventually surfaces as a restore that loses content; a field missing from the detail read surfaces as a
/// creator who cannot see something they typed, with no error anywhere and nothing in a log. Both are driven
/// from the EF model and reflection rather than from a hand-picked list, so a field added anywhere is covered
/// without anyone extending anything.
/// </para>
/// <para>
/// <see cref="NotPublished"/> is the deliberate seam, and the reason each exclusion carries a sentence:
/// withholding a field is a decision, and a decision belongs in a diff. Forgetting one is invisible.
/// </para>
/// </remarks>
public sealed class RecipeDetailCompletenessTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// Entity properties the detail read deliberately does not publish, and why.
    /// </summary>
    private static readonly Dictionary<string, string> NotPublished = new()
    {
        ["WorkspaceId"] = "ownership; resolved server-side and never named by either direction of a request",
        ["RecipeId"] = "parentage, already implied by the nesting the client reads",
        ["RecipeIngredientGroupId"] = "parentage, as above",
        ["RecipeInstructionGroupId"] = "parentage, as above",
        ["CreatedByMembershipId"] = "authorship as an internal membership id; showing an author needs a display name, not this",
        ["UpdatedByMembershipId"] = "authorship, as above",
        ["RowVersion"] = "published only as the opaque ConcurrencyToken, never as raw bytes",
        ["YieldUnitDimension"] = "denormalized to carry a composite foreign key; a fact about the unit, readable from the unit",
        ["MeasurementUnitDimension"] = "denormalized for the composite foreign key, as above",
        ["TemperatureUnitDimension"] = "denormalized for the composite foreign key, as above",
    };

    /// <summary>
    /// Entity properties the version summary deliberately does not publish, and why.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="NotPublished"/> because a version is not content: most of what it records is
    /// about history, and history has its own seams. An exclusion excused here should not silently excuse a
    /// field of the same name on the recipe itself.
    /// </remarks>
    private static readonly Dictionary<string, string> VersionNotPublished = new()
    {
        ["WorkspaceId"] = "ownership, as on the recipe",
        ["RecipeId"] = "parentage; the client already knows which recipe it asked for",
        ["CreatedByMembershipId"] = "authorship as an internal membership id",
        ["ParentVersionId"] = "lineage, which belongs to the history and diff seams",
        ["BasedOnRecipeRowVersion"] = "lineage, as above, and a concurrency token of a superseded state",
        ["AiProposalId"] = "provenance of a proposal, published by the AI seam that owns proposals",
        ["SnapshotSchemaVersion"] = "how the archive is encoded; an internal detail of the archive",
    };

    /// <summary>The live entities whose content the detail read must publish, and the model that holds each.</summary>
    /// <remarks>
    /// <see cref="WorkspaceTag"/> is absent, and that is not an oversight. It is an aggregate root of its own —
    /// a vocabulary that is listed, renamed and retired without reference to any recipe — and this read borrows
    /// one field of it to name a chip. Pairing it here would assert that a recipe read owes a client the whole
    /// tag vocabulary, which is a different seam's promise to make.
    /// </remarks>
    private static readonly (string Entity, string Model)[] PublishedPairs =
    [
        (nameof(Recipe), nameof(RecipeDetailServiceModel)),
        (nameof(RecipeIngredientGroup), nameof(RecipeIngredientGroupServiceModel)),
        (nameof(RecipeIngredient), nameof(RecipeIngredientServiceModel)),
        (nameof(RecipeInstructionGroup), nameof(RecipeInstructionGroupServiceModel)),
        (nameof(RecipeInstructionStep), nameof(RecipeInstructionStepServiceModel)),
        (nameof(RecipeEquipment), nameof(RecipeEquipmentServiceModel)),
        (nameof(RecipeAssetLink), nameof(RecipeAssetLinkServiceModel)),
        (nameof(RecipeTag), nameof(RecipeTagServiceModel)),
        (nameof(RecipeVersion), nameof(RecipeVersionSummaryServiceModel)),
    ];

    public static TheoryData<string, string> PublishedEntities()
    {
        var data = new TheoryData<string, string>();

        foreach (var (entity, model) in PublishedPairs)
        {
            data.Add(entity, model);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PublishedEntities))]
    public void Every_persisted_field_is_either_published_or_explicitly_withheld(string entityName, string modelName)
    {
        var excluded = entityName == nameof(RecipeVersion) ? VersionNotPublished : NotPublished;
        var published = ServiceModel(modelName)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = EntityType(entityName).GetProperties()
            .Where(property => !property.IsShadowProperty())
            .Select(property => property.Name)
            .Where(name => !excluded.ContainsKey(name))
            .Where(name => !published.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{entityName} persists fields the detail read does not publish, so a creator cannot see them: "
                + $"{string.Join(", ", missing)}. Either add them to {modelName}, or add them to the withheld "
                + "list with the reason they must not be published.");
    }

    /// <summary>
    /// An exclusion for a field no entity has any more is a permanently widened hole: the day something is
    /// added under that name, it is excused before anyone reads the diff.
    /// </summary>
    [Theory]
    [InlineData(nameof(NotPublished))]
    [InlineData(nameof(VersionNotPublished))]
    public void Every_withheld_name_is_a_real_field_on_some_entity(string listName)
    {
        var (excluded, entities) = listName == nameof(VersionNotPublished)
            ? (VersionNotPublished, (string[])[nameof(RecipeVersion)])
            : (NotPublished, [.. PublishedPairs.Select(pair => pair.Entity).Where(name => name != nameof(RecipeVersion))]);

        var everyPersistedName = entities
            .SelectMany(name => EntityType(name).GetProperties())
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var phantom = excluded.Keys.Where(name => !everyPersistedName.Contains(name)).ToList();

        Assert.True(
            phantom.Count == 0,
            $"{listName} excuses names that no entity has any more: {string.Join(", ", phantom)}. Remove them, "
                + "or the next field added under one of those names is withheld unnoticed.");
    }

    /// <summary>
    /// The half the field-by-field check cannot see: a mapper that reads the right entity property and writes
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Every source field is set to something non-default by
    /// <see cref="RecipeAggregateFixture.FullyPopulatedRecipe"/>, so any published field still sitting at its
    /// default was never written. Without this, a mapper that mapped <c>Headnote</c> to nothing would satisfy
    /// the theory above — the property exists on the model, which is all that check can prove.
    /// </remarks>
    [Fact]
    public void Publishing_a_fully_populated_recipe_leaves_no_field_at_its_default()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = 4,
            Source = RecipeVersionSource.Import,
            Readiness = RecipeVersionReadiness.Ready,
            Reason = "Imported from the old site.",
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = RecipeAggregateFixture.Now,
        };
        var tag = new WorkspaceTag
        {
            Id = RecipeAggregateFixture.TagIdA,
            Name = "Weeknight",
            NormalizedName = "weeknight",
            CreatedAt = RecipeAggregateFixture.Now,
        };

        var detail = RecipeDetailMapper.ToDetail(new TaggedRecipe(new CompleteRecipe(recipe, version), [tag]));

        var unwritten = new List<string>();
        AssertNoDefaults(detail, detail.GetType().Name, unwritten);

        Assert.True(
            unwritten.Count == 0,
            "The mapper left these published fields at their default value even though the source recipe set "
                + $"every field: {string.Join(", ", unwritten)}");
    }

    private static void AssertNoDefaults(object instance, string path, List<string> unwritten)
    {
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = property.GetValue(instance);

            if (value is System.Collections.IEnumerable nested and not string)
            {
                var items = nested.Cast<object>().ToList();

                if (items.Count == 0)
                {
                    unwritten.Add($"{path}.{property.Name} (empty)");
                    continue;
                }

                AssertNoDefaults(items[0], $"{path}.{property.Name}[0]", unwritten);
                continue;
            }

            if (value is null)
            {
                unwritten.Add($"{path}.{property.Name} (null)");
                continue;
            }

            if (value is string { Length: 0 })
            {
                unwritten.Add($"{path}.{property.Name} (empty string)");
                continue;
            }

            if (property.PropertyType.IsValueType && value.Equals(Activator.CreateInstance(property.PropertyType)))
            {
                unwritten.Add($"{path}.{property.Name} (default)");
                continue;
            }

            if (!property.PropertyType.IsValueType && property.PropertyType != typeof(string))
            {
                AssertNoDefaults(value, $"{path}.{property.Name}", unwritten);
            }
        }
    }

    private static Type ServiceModel(string name) =>
        typeof(RecipeDetailServiceModel).Assembly.GetTypes().Single(type => type.Name == name);

    private IEntityType EntityType(string name)
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model.GetEntityTypes().Single(type => type.ClrType.Name == name);
    }
}
