using System.Reflection;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Makes "add a column to an entity and forget to archive it" a failing test rather than something a
/// reviewer has to notice.
/// </summary>
/// <remarks>
/// <para>
/// Every other snapshot test compares a hand-picked set of fields, which means a mapper that silently
/// dropped one would pass all of them. These tests are driven from the EF model and from reflection over
/// the document records, so a field added anywhere is covered without anyone extending a list.
/// </para>
/// <para>
/// The <see cref="NotArchived"/> list is the deliberate seam. Leaving a field out of the archive is a real
/// decision — ownership and audit columns must not be restorable — and putting a name there makes that
/// decision a visible line in a diff. Forgetting a field, by contrast, is invisible. That asymmetry is the
/// entire point.
/// </para>
/// </remarks>
public sealed class RecipeSnapshotCompletenessTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// Entity properties that deliberately do not belong in a snapshot, and why.
    /// </summary>
    private static readonly Dictionary<string, string> NotArchived = new()
    {
        ["Id"] = "identity of the live row; the caller supplies the recipe a restore targets",
        ["WorkspaceId"] = "ownership; a snapshot that carried it could move a recipe between workspaces",
        ["RecipeId"] = "parentage of the live row, re-derived on restore",
        ["RecipeIngredientGroupId"] = "parentage, re-derived from the document's own nesting",
        ["RecipeInstructionGroupId"] = "parentage, re-derived from the document's own nesting",
        ["CreatedByMembershipId"] = "authorship; restoring content must not rewrite who wrote it",
        ["UpdatedByMembershipId"] = "authorship, as above",
        ["CreatedAt"] = "audit time of the live row, not of the content",
        ["UpdatedAt"] = "audit time, as above",
        ["RowVersion"] = "concurrency token of the live row; the version records its own copy",
    };

    /// <summary>The live entities whose content a snapshot must reproduce, and the record that holds each.</summary>
    private static readonly (string Entity, string Record)[] ArchivedPairs =
    [
        (nameof(Recipe), nameof(RecipeSnapshotHeader)),
        (nameof(RecipeIngredientGroup), nameof(RecipeSnapshotIngredientGroup)),
        (nameof(RecipeIngredient), nameof(RecipeSnapshotIngredient)),
        (nameof(RecipeInstructionGroup), nameof(RecipeSnapshotInstructionGroup)),
        (nameof(RecipeInstructionStep), nameof(RecipeSnapshotInstructionStep)),
        (nameof(RecipeEquipment), nameof(RecipeSnapshotEquipment)),
        (nameof(RecipeAssetLink), nameof(RecipeSnapshotAssetLink)),
        (nameof(RecipeTag), nameof(RecipeSnapshotTag)),
    ];

    public static TheoryData<string, string> ArchivedEntities()
    {
        var data = new TheoryData<string, string>();

        foreach (var (entity, record) in ArchivedPairs)
        {
            data.Add(entity, record);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ArchivedEntities))]
    public void Every_persisted_field_is_either_archived_or_explicitly_excluded(string entityName, string recordName)
    {
        var entityType = Model().GetEntityTypes().Single(type => type.ClrType.Name == entityName);
        var documentProperties = DocumentRecord(recordName)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = entityType.GetProperties()
            .Where(property => !property.IsShadowProperty())
            .Select(property => property.Name)
            .Where(name => !NotArchived.ContainsKey(name))
            .Where(name => !documentProperties.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{entityName} persists fields the snapshot does not archive, so a version of this recipe would "
                + $"not reconstruct it: {string.Join(", ", missing)}. Either add them to {recordName}, or add "
                + $"them to {nameof(NotArchived)} with the reason they must not be restorable.");
    }

    /// <summary>
    /// An exclusion for a field no entity has any more is a permanently widened hole: the day something is
    /// added under that name, it is excused before anyone reads the diff.
    /// </summary>
    [Fact]
    public void Every_excluded_name_is_a_real_field_on_some_entity()
    {
        var everyPersistedName = Model().GetEntityTypes()
            .Where(type => ArchivedPairs.Select(pair => pair.Entity).Contains(type.ClrType.Name))
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var phantom = NotArchived.Keys.Where(name => !everyPersistedName.Contains(name)).ToList();

        Assert.True(
            phantom.Count == 0,
            $"{nameof(NotArchived)} excuses names that no entity has any more: {string.Join(", ", phantom)}. "
                + "Remove them, or the next field added under one of those names is excused unnoticed.");
    }

    [Fact]
    public void A_fully_populated_recipe_survives_a_round_trip_field_for_field()
    {
        var original = RecipeAggregateFixture.FullyPopulatedRecipe();

        var captured = RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(original));
        var restored = RecipeSnapshotMapper.Restore(
            RecipeSnapshotSerializer.Deserialize(RecipeSnapshotSerializer.Serialize(captured)),
            original.Id);

        // Comparing the serialized documents rather than the records: these hold IReadOnlyList members, and
        // record equality falls back to reference equality on those, so `==` would pass on a mapper that
        // dropped every child.
        Assert.Equal(
            RecipeSnapshotSerializer.Serialize(captured),
            RecipeSnapshotSerializer.Serialize(RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(restored))));
    }

    [Fact]
    public void Capturing_a_fully_populated_recipe_leaves_no_document_field_at_its_default()
    {
        var document = RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(RecipeAggregateFixture.FullyPopulatedRecipe()));

        // The round-trip test above cannot see a field that Capture drops — both sides lose it identically
        // and still match. This is the half that catches that: with every source field set to something
        // non-default, any document field still sitting at its default was never written.
        var unwritten = new List<string>();
        AssertNoDefaults(document, document.GetType().Name, unwritten);

        Assert.True(
            unwritten.Count == 0,
            $"Capture left these document fields at their default value even though the source recipe set "
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

    private static Type DocumentRecord(string name) =>
        typeof(RecipeSnapshotDocument).Assembly.GetTypes().Single(type => type.Name == name);

    private IModel Model()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        return RecipeAggregateFixture.Db(scope).Model;
    }
}
