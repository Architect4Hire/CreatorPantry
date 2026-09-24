using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The comparison has to be right about six things to be usable at all: what was added, what was removed,
/// what was rewritten, what was reordered, what moved between groups, and — the one a diff is most often
/// wrong about — what did not change.
/// </summary>
/// <remarks>
/// Everything here runs on documents built in memory. The comparer touches no database, no clock and no
/// model, so a test that needed any of those would be testing something else.
/// </remarks>
public sealed class RecipeComparerTests
{
    [Fact]
    public void Comparing_a_document_with_itself_reports_no_change()
    {
        var document = Capture(RecipeAggregateFixture.FullyPopulatedRecipe());

        var comparison = RecipeComparer.Compare(document, document);

        Assert.False(comparison.HasChanges);
        Assert.All(comparison.Sections, section => Assert.False(section.HasChanges));
    }

    [Fact]
    public void Every_section_is_present_even_when_nothing_in_it_changed()
    {
        var document = Capture(RecipeAggregateFixture.NewRecipe("Olive oil cake"));

        var comparison = RecipeComparer.Compare(document, document);

        // A comparison panel renders a stable frame and says "no change" per section, so an absent section
        // would be indistinguishable from a section the comparer forgot.
        Assert.Equal(
            Enum.GetValues<RecipeComparisonSection>(),
            comparison.Sections.Select(section => section.Section).ToArray());
    }

    [Fact]
    public void An_added_ingredient_line_is_reported_with_its_content()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var before = Capture(recipe);

        var group = recipe.IngredientGroups.Single();
        var added = Line(recipe, group, "a pinch of flaky salt");
        group.Ingredients.Add(added);

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        Assert.Equal(added.Id, change.Id);
        Assert.Equal(RecipeItemPresence.Added, change.Presence);
        Assert.False(change.Moved);
        Assert.Null(change.FromRank);
        Assert.Equal(1, change.ToRank);
        Assert.Equal(group.Id, change.ToParentId);
        Assert.Equal(
            "a pinch of flaky salt",
            change.FieldChanges.Single(field => field.Field == RecipeComparisonField.IngredientDisplayText).To);
    }

    [Fact]
    public void An_added_item_does_not_announce_the_fields_it_left_unsaid()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var before = Capture(recipe);

        var group = recipe.IngredientGroups.Single();
        group.Ingredients.Add(Line(recipe, group, "a pinch of flaky salt"));

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        // "Not optional" and "no match was attempted" are the absence of a claim, not a claim. Reporting them
        // would bury the one line of content a reviewer is actually reading.
        Assert.DoesNotContain(change.FieldChanges, field => field.Field == RecipeComparisonField.IngredientIsOptional);
        Assert.DoesNotContain(change.FieldChanges, field => field.Field == RecipeComparisonField.IngredientMatchStatus);
    }

    [Fact]
    public void A_removed_ingredient_line_is_reported_with_the_content_it_took_with_it()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var group = recipe.IngredientGroups.Single();
        var removed = group.Ingredients.Single();
        var before = Capture(recipe);

        group.Ingredients.Clear();

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        Assert.Equal(removed.Id, change.Id);
        Assert.Equal(RecipeItemPresence.Removed, change.Presence);
        Assert.Equal(0, change.FromRank);
        Assert.Null(change.ToRank);
        Assert.Equal(group.Id, change.FromParentId);

        // A reviewer has to be able to see what was deleted without being handed both source documents.
        var text = change.FieldChanges.Single(field => field.Field == RecipeComparisonField.IngredientDisplayText);
        Assert.Equal(removed.DisplayText, text.From);
        Assert.Null(text.To);
    }

    [Fact]
    public void Removing_a_line_does_not_report_the_lines_beneath_it_as_moved()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var group = recipe.IngredientGroups.Single();
        group.Ingredients.Clear();

        var first = Add(recipe, group, "240 g flour");
        Add(recipe, group, "3 eggs");
        Add(recipe, group, "200 ml olive oil");

        var before = Capture(recipe);
        group.Ingredients.Remove(first);
        Renumber(group);

        var changes = Changes(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        // The whole reason movement is judged relative to the survivors. A rank comparison would report both
        // remaining lines as having moved, and every deletion would read as a reshuffle of the group.
        Assert.Equal(RecipeItemPresence.Removed, Assert.Single(changes).Presence);
        Assert.Equal(first.Id, changes[0].Id);
    }

    [Fact]
    public void A_reworded_line_is_a_field_change_and_not_a_move()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var line = recipe.IngredientGroups.Single().Ingredients.Single();
        var before = Capture(recipe);

        line.DisplayText = "2 cups (240 g) all-purpose flour, sifted twice";

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        Assert.Equal(RecipeItemPresence.Retained, change.Presence);
        Assert.False(change.Moved);

        var text = Assert.Single(change.FieldChanges);
        Assert.Equal(RecipeComparisonField.IngredientDisplayText, text.Field);
        Assert.Equal("2 cups (240 g) all-purpose flour, sifted", text.From);
        Assert.Equal("2 cups (240 g) all-purpose flour, sifted twice", text.To);
    }

    [Fact]
    public void Reordering_lines_reports_a_move_and_no_text_change()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var group = recipe.IngredientGroups.Single();
        group.Ingredients.Clear();

        var flour = Add(recipe, group, "240 g flour");
        var eggs = Add(recipe, group, "3 eggs");
        var oil = Add(recipe, group, "200 ml olive oil");

        var before = Capture(recipe);

        // flour, eggs, oil -> oil, flour, eggs. One line was dragged to the top.
        oil.SortOrder = 0;
        flour.SortOrder = 1;
        eggs.SortOrder = 2;

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        Assert.Equal(oil.Id, change.Id);
        Assert.Equal(RecipeItemPresence.Retained, change.Presence);
        Assert.True(change.Moved);
        Assert.Equal(2, change.FromRank);
        Assert.Equal(0, change.ToRank);

        // The restriction this whole design turns on: a reorder is never reported as a replacement.
        Assert.Empty(change.FieldChanges);
    }

    [Fact]
    public void Dragging_one_line_across_many_reports_one_move()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var group = recipe.IngredientGroups.Single();
        group.Ingredients.Clear();

        var lines = Enumerable.Range(0, 10).Select(i => Add(recipe, group, $"ingredient {i}")).ToList();

        var before = Capture(recipe);

        // Move the first line to the end. Nine lines shift up by one; none of them changed places relative
        // to each other, and a reviewer must be shown one move rather than ten.
        lines[0].SortOrder = 100;
        Renumber(group);

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        Assert.Equal(lines[0].Id, change.Id);
        Assert.True(change.Moved);
        Assert.Equal(9, change.ToRank);
    }

    [Fact]
    public void A_line_moved_to_another_group_is_matched_to_itself()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var dough = recipe.IngredientGroups.Single();
        var topping = new RecipeIngredientGroup { Id = Guid.NewGuid(), RecipeId = recipe.Id, Title = "For the topping", SortOrder = 1 };
        recipe.IngredientGroups.Add(topping);

        var line = dough.Ingredients.Single();
        var before = Capture(recipe);

        dough.Ingredients.Remove(line);
        line.RecipeIngredientGroupId = topping.Id;
        line.SortOrder = 0;
        topping.Ingredients.Add(line);

        var changes = Changes(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);
        var moved = Assert.Single(changes, change => change.Id == line.Id);

        // Not a removal from one group plus an unrelated insertion into another: ids are matched across the
        // whole document, so the line is recognised as the same line and its text is not reported as new.
        Assert.Equal(RecipeItemPresence.Retained, moved.Presence);
        Assert.True(moved.Moved);
        Assert.Equal(dough.Id, moved.FromParentId);
        Assert.Equal(topping.Id, moved.ToParentId);
        Assert.Empty(moved.FieldChanges);
    }

    [Fact]
    public void A_line_that_was_reworded_and_moved_reports_both()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var group = recipe.IngredientGroups.Single();
        group.Ingredients.Clear();

        Add(recipe, group, "240 g flour");
        Add(recipe, group, "3 eggs");
        var oil = Add(recipe, group, "200 ml olive oil");

        var before = Capture(recipe);

        // Three lines rather than two, and the last dragged to the front: a two-line swap has two equally
        // minimal descriptions and the algorithm is free to pick either, so it cannot carry an assertion
        // about which line moved. Pinned separately, in A_swap_names_one_of_the_two_lines_deterministically.
        oil.SortOrder = -1;
        oil.DisplayText = "200 ml fruity olive oil";

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        Assert.Equal(oil.Id, change.Id);
        Assert.True(change.Moved);
        Assert.Equal(RecipeComparisonField.IngredientDisplayText, Assert.Single(change.FieldChanges).Field);
    }

    [Fact]
    public void A_swap_names_one_of_the_two_lines_deterministically()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var group = recipe.IngredientGroups.Single();
        group.Ingredients.Clear();

        var flour = Add(recipe, group, "240 g flour");
        var eggs = Add(recipe, group, "3 eggs");

        var before = Capture(recipe);
        eggs.SortOrder = -1;

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        // "flour and eggs swapped" is equally truthfully described as either one having moved, and the
        // comparison reports one rather than both. Which one is an artefact of the subsequence search, not a
        // claim about what the creator did — but it must be the same artefact every time, or two reads of
        // the same pair of versions would show a creator two different diffs.
        Assert.Equal(flour.Id, change.Id);
        Assert.True(change.Moved);
        Assert.Empty(change.FieldChanges);
    }

    [Fact]
    public void Reordering_groups_says_nothing_about_the_lines_they_carry()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var filling = Group(recipe, "For the filling", sortOrder: 1);
        var topping = Group(recipe, "For the topping", sortOrder: 2);
        Add(recipe, filling, "300 g plums");
        Add(recipe, topping, "2 tbsp demerara sugar");

        var before = Capture(recipe);

        topping.SortOrder = -1;

        var change = Single(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Ingredients);

        // Movement is judged within a parent, so a group that moved takes its lines with it silently: the
        // one change reported is the group, and nothing at all is said about the four lines beneath the
        // three groups.
        Assert.Equal(topping.Id, change.Id);
        Assert.True(change.Moved);
        Assert.Null(change.ToParentId);
    }

    [Fact]
    public void Renumbering_sort_orders_is_not_a_change()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var group = recipe.IngredientGroups.Single();
        group.Ingredients.Clear();
        var flour = Add(recipe, group, "240 g flour");
        var eggs = Add(recipe, group, "3 eggs");
        flour.SortOrder = 0;
        eggs.SortOrder = 10;

        var before = Capture(recipe);

        // An editor that saves positions densely where the last save left gaps has moved nothing.
        eggs.SortOrder = 1;

        Assert.False(RecipeComparer.Compare(before, Capture(recipe)).HasChanges);
    }

    [Fact]
    public void A_quantity_that_only_changed_decimal_scale_is_not_a_change()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var line = recipe.IngredientGroups.Single().Ingredients.Single();
        line.Quantity = 240m;
        var before = Capture(recipe);

        line.Quantity = 240.00m;

        // Detection is on the typed value, not on its rendering, so a scale that survived a serialization
        // round trip does not read as an edit to the recipe.
        Assert.False(RecipeComparer.Compare(before, Capture(recipe)).HasChanges);
    }

    [Fact]
    public void Header_fields_land_in_the_section_a_creator_reads_them_in()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var before = Capture(recipe);

        recipe.Title = "Olive oil and rosemary cake";
        recipe.CookTimeMinutes = 30;
        recipe.YieldText = "makes one 9-inch cake";
        recipe.StorageNotes = "Keeps three days under a cloth.";
        recipe.Status = RecipeStatus.Ready;

        var comparison = RecipeComparer.Compare(before, Capture(recipe));

        Assert.Equal(RecipeComparisonField.Title, Assert.Single(comparison[RecipeComparisonSection.Metadata].FieldChanges).Field);
        Assert.Equal(RecipeComparisonField.CookTimeMinutes, Assert.Single(comparison[RecipeComparisonSection.Timing].FieldChanges).Field);
        Assert.Equal(RecipeComparisonField.YieldText, Assert.Single(comparison[RecipeComparisonSection.Yield].FieldChanges).Field);
        Assert.Equal(RecipeComparisonField.StorageNotes, Assert.Single(comparison[RecipeComparisonSection.Notes].FieldChanges).Field);

        var status = Assert.Single(comparison[RecipeComparisonSection.Publication].FieldChanges);
        Assert.Equal("Draft", status.From);
        Assert.Equal("Ready", status.To);
    }

    [Fact]
    public void Steps_equipment_and_media_are_compared_too()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var before = Capture(recipe);

        recipe.InstructionGroups.Single().Steps.Single().DurationMinutes = 30;
        recipe.Equipment.Single().Note = "A loaf tin also works.";
        recipe.AssetLinks.Clear();

        var comparison = RecipeComparer.Compare(before, Capture(recipe));

        Assert.Equal(
            RecipeComparisonField.StepDurationMinutes,
            Assert.Single(Single(comparison, RecipeComparisonSection.Instructions).FieldChanges).Field);
        Assert.Equal(
            RecipeComparisonField.EquipmentNote,
            Assert.Single(Single(comparison, RecipeComparisonSection.Equipment).FieldChanges).Field);

        // A recipe that lost its hero image has changed, and a comparison that stayed quiet about it would
        // be telling a creator their version is identical when it is not.
        Assert.Equal(RecipeItemPresence.Removed, Single(comparison, RecipeComparisonSection.Media).Presence);
    }

    [Fact]
    public void Tags_are_added_and_removed_and_never_moved()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var kept = new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = Guid.NewGuid() };
        var dropped = new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = Guid.NewGuid() };
        recipe.Tags.Add(kept);
        recipe.Tags.Add(dropped);

        var before = Capture(recipe);

        var introduced = Guid.NewGuid();
        recipe.Tags.Remove(dropped);
        recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = introduced });

        var changes = Changes(RecipeComparer.Compare(before, Capture(recipe)), RecipeComparisonSection.Metadata);

        Assert.Equal(2, changes.Count);
        Assert.Equal(RecipeItemPresence.Added, Assert.Single(changes, change => change.Id == introduced).Presence);
        Assert.Equal(RecipeItemPresence.Removed, Assert.Single(changes, change => change.Id == dropped.WorkspaceTagId).Presence);
        Assert.DoesNotContain(changes, change => change.Id == kept.WorkspaceTagId);

        // A tag has no order to move within, so a rank on one would be a number a reader could believe.
        Assert.All(changes, change =>
        {
            Assert.False(change.Moved);
            Assert.Null(change.FromRank);
            Assert.Null(change.ToRank);
        });
    }

    [Fact]
    public void Two_readable_but_different_schema_versions_compare_cleanly()
    {
        var document = Capture(RecipeAggregateFixture.NewRecipe("Olive oil cake"));

        // Only one shape exists today, so this asserts the weaker but still meaningful thing: the comparer
        // reads each document's own declared version rather than assuming both are current. When
        // CurrentSchemaVersion becomes 2 this is where a v1-against-v2 comparison is proved.
        Assert.False(RecipeComparer.Compare(document, document with { SchemaVersion = 1 }).HasChanges);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RecipeSnapshotDocument.CurrentSchemaVersion + 1)]
    public void A_document_this_build_cannot_read_is_refused(int schemaVersion)
    {
        var document = Capture(RecipeAggregateFixture.NewRecipe("Olive oil cake"));
        var unreadable = document with { SchemaVersion = schemaVersion };

        Assert.Throws<NotSupportedException>(() => RecipeComparer.Compare(unreadable, document));
        Assert.Throws<NotSupportedException>(() => RecipeComparer.Compare(document, unreadable));
    }

    [Fact]
    public void A_document_that_repeats_an_identifier_is_refused()
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Olive oil cake");
        var group = recipe.IngredientGroups.Single();
        var duplicate = Line(recipe, group, "3 eggs");
        duplicate.Id = group.Ingredients.Single().Id;
        group.Ingredients.Add(duplicate);

        var corrupt = Capture(recipe);

        // Identifiers are what the comparison matches on. Choosing one of two rows carrying the same id
        // would produce a diff describing a recipe that never existed.
        Assert.Throws<InvalidOperationException>(() => RecipeComparer.Compare(corrupt, corrupt));
    }

    [Fact]
    public void Comparing_the_same_pair_twice_produces_the_same_answer()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        var before = Capture(recipe);

        var group = recipe.IngredientGroups.Single();
        var eggs = Add(recipe, group, "3 eggs");
        Add(recipe, group, "200 ml olive oil");
        eggs.SortOrder = -1;
        recipe.Title = "Olive oil and rosemary cake";

        var after = Capture(recipe);

        // Serialized rather than compared as records: the result holds lists, whose record equality is by
        // reference, so an identity comparison here would pass without proving anything. The claim being
        // made is that the whole answer is reproducible — the move detection above all, which chooses
        // between equally minimal descriptions and must choose the same one every time.
        Assert.Equal(
            JsonSerializer.Serialize(RecipeComparer.Compare(before, after)),
            JsonSerializer.Serialize(RecipeComparer.Compare(before, after)));
    }

    private static RecipeSnapshotDocument Capture(Recipe recipe) =>
        RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe));

    private static IReadOnlyList<RecipeItemChange> Changes(RecipeComparison comparison, RecipeComparisonSection section) =>
        comparison[section].ItemChanges;

    private static RecipeItemChange Single(RecipeComparison comparison, RecipeComparisonSection section) =>
        Assert.Single(comparison[section].ItemChanges);

    /// <summary>An empty ingredient group added to the recipe.</summary>
    private static RecipeIngredientGroup Group(Recipe recipe, string title, int sortOrder)
    {
        var group = new RecipeIngredientGroup
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            Title = title,
            SortOrder = sortOrder,
        };

        recipe.IngredientGroups.Add(group);

        return group;
    }

    /// <summary>A line appended to a group, positioned after whatever the group already holds.</summary>
    private static RecipeIngredient Add(Recipe recipe, RecipeIngredientGroup group, string displayText)
    {
        var line = Line(recipe, group, displayText);
        group.Ingredients.Add(line);

        return line;
    }

    /// <summary>A detached line, for the cases that need one before it joins a group.</summary>
    private static RecipeIngredient Line(Recipe recipe, RecipeIngredientGroup group, string displayText) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = recipe.Id,
        RecipeIngredientGroupId = group.Id,
        SortOrder = group.Ingredients.Count,
        DisplayText = displayText,
    };

    /// <summary>Renumbers a group densely, the way an editor saving a reordered list would.</summary>
    private static void Renumber(RecipeIngredientGroup group)
    {
        var ordered = group.Ingredients.OrderBy(line => line.SortOrder).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
        }
    }
}
