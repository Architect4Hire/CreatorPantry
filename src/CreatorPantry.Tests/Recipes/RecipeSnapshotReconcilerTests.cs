using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What a restore does to a recipe's content, asserted without a database: the reconciler is pure, so every
/// claim here is about object graphs and none of it needs a save to be true.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing test is <see cref="A_restored_recipe_captures_back_to_the_document_it_was_restored_from"/>.
/// Every other test here checks one behaviour by hand; that one states the whole contract — after a restore,
/// capturing the recipe produces the document byte for byte — and it is driven from
/// <c>FullyPopulatedRecipe</c>, so a field added to the aggregate and forgotten in the reconciler fails it
/// without anyone extending a list.
/// </para>
/// <para>
/// <strong>Every test archives the recipe it then restores</strong>, through <see cref="Archive"/>. That is
/// not tidiness: <c>FullyPopulatedRecipe</c> mints fresh ids on each call, so a document captured from a
/// second call would share no child identity with the live recipe, and every test would silently become a
/// test of "replace all the children" rather than of reconciliation.
/// </para>
/// <para>
/// <c>WorkspaceId</c> is asserted explicitly on entities the reconciler adds, because the alternate keys on
/// the group entities are set before <c>WorkspaceOwnershipInterceptor</c> would run and a missing value there
/// is an EF failure at save time rather than a wrong answer here.
/// </para>
/// </remarks>
public sealed class RecipeSnapshotReconcilerTests
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();

    private static readonly Guid TagA = RecipeAggregateFixture.TagIdA;

    private static readonly Guid TagB = RecipeAggregateFixture.TagIdB;

    /// <summary>The vocabulary rows for every tag these tests use, so nothing is dropped by accident.</summary>
    private static readonly WorkspaceTag[] AllTags =
    [
        new() { Id = TagA, WorkspaceId = WorkspaceId, Name = "Weeknight", NormalizedName = "weeknight", CreatedAt = RecipeAggregateFixture.Now },
        new() { Id = TagB, WorkspaceId = WorkspaceId, Name = "Autumn", NormalizedName = "autumn", CreatedAt = RecipeAggregateFixture.Now },
    ];

    /// <summary>
    /// A live recipe and the document that archives it — captured from that very recipe, so every child
    /// identity matches, which is what a real version's snapshot looks like.
    /// </summary>
    private static (Recipe Live, RecipeSnapshotDocument Archived) Archive()
    {
        var live = Live();

        return (live, Capture(live));
    }

    /// <summary>A recipe as it would be loaded: fully populated and owned by a resolved workspace.</summary>
    private static Recipe Live()
    {
        var recipe = RecipeAggregateFixture.FullyPopulatedRecipe();
        Own(recipe);

        return recipe;
    }

    /// <summary>
    /// Stamps the workspace the interceptor would have stamped when this aggregate was first saved, so the
    /// reconciler has a value to copy onto anything it adds.
    /// </summary>
    private static void Own(Recipe recipe)
    {
        recipe.WorkspaceId = WorkspaceId;

        foreach (var group in recipe.IngredientGroups)
        {
            group.WorkspaceId = WorkspaceId;

            foreach (var line in group.Ingredients)
            {
                line.WorkspaceId = WorkspaceId;
            }
        }

        foreach (var group in recipe.InstructionGroups)
        {
            group.WorkspaceId = WorkspaceId;

            foreach (var step in group.Steps)
            {
                step.WorkspaceId = WorkspaceId;
            }
        }

        foreach (var item in recipe.Equipment)
        {
            item.WorkspaceId = WorkspaceId;
        }

        foreach (var link in recipe.AssetLinks)
        {
            link.WorkspaceId = WorkspaceId;
        }

        foreach (var tag in recipe.Tags)
        {
            tag.WorkspaceId = WorkspaceId;
        }
    }

    private static RecipeSnapshotDocument Capture(Recipe recipe) =>
        RecipeSnapshotMapper.Capture(RecipeAggregateFixture.Complete(recipe));

    private static bool Apply(Recipe recipe, RecipeSnapshotDocument document) =>
        RecipeSnapshotReconciler.Apply(recipe, document, AllTags);

    // ---- The whole contract, in one assertion ----

    /// <summary>
    /// After a restore, the recipe says exactly what the document said. Checked by capturing it again and
    /// comparing the serialized documents, which is the strongest available statement: the version a restore
    /// writes is built by exactly that capture, so this is the property that makes a later diff of the
    /// restored version against its source come out empty.
    /// </summary>
    [Fact]
    public void A_restored_recipe_captures_back_to_the_document_it_was_restored_from()
    {
        var (live, archived) = Archive();

        Ruin(live);

        Assert.True(Apply(live, archived));
        Assert.Equal(
            RecipeSnapshotSerializer.Serialize(archived),
            RecipeSnapshotSerializer.Serialize(Capture(live)));
    }

    /// <summary>
    /// Changes every part of the aggregate a restore has to be able to undo: each header field, a line's
    /// text and readings, a step's temperature, a group's heading and order, an added child, a removed child
    /// and the tag set.
    /// </summary>
    private static void Ruin(Recipe recipe)
    {
        recipe.Title = "Something else entirely";
        recipe.Description = null;
        recipe.Headnote = "Rewritten.";
        recipe.Notes = null;
        recipe.StorageNotes = null;
        recipe.AttributionText = null;
        recipe.SourceUrl = null;
        recipe.CuisineId = null;
        recipe.CourseId = null;
        recipe.PrimaryTechniqueId = null;
        recipe.PrepTimeMinutes = 999;
        recipe.CookTimeMinutes = null;
        recipe.RestTimeMinutes = null;
        recipe.TotalTimeMinutes = 999;
        recipe.YieldText = "serves a crowd";
        recipe.YieldQuantity = null;
        recipe.YieldUnitId = null;
        recipe.YieldUnitDimension = null;
        recipe.Status = RecipeStatus.Archived;

        var ingredientGroup = recipe.IngredientGroups.Single();
        ingredientGroup.Title = "Renamed group";
        ingredientGroup.SortOrder = 11;

        var line = ingredientGroup.Ingredients.Single();
        line.DisplayText = "a different ingredient entirely";
        line.IngredientNameText = null;
        line.Quantity = 1m;
        line.QuantityUpper = null;
        line.MeasurementUnitId = null;
        line.MeasurementUnitDimension = null;
        line.IngredientId = null;
        line.MatchStatus = IngredientMatchStatus.NoMatch;
        line.PreparationNote = null;
        line.IsOptional = false;
        line.ScalingBehavior = IngredientScaling.Proportional;
        line.SortOrder = 11;

        // An added line, which the restore must remove because the document does not name it.
        ingredientGroup.Ingredients.Add(new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            WorkspaceId = recipe.WorkspaceId,
            RecipeId = recipe.Id,
            RecipeIngredientGroupId = ingredientGroup.Id,
            SortOrder = 12,
            DisplayText = "something the creator added later",
        });

        var instructionGroup = recipe.InstructionGroups.Single();
        instructionGroup.Title = "Renamed method";
        instructionGroup.SortOrder = 11;

        var step = instructionGroup.Steps.Single();
        step.Text = "Do something else.";
        step.TechniqueId = null;
        step.DurationMinutes = null;
        step.TemperatureValue = null;
        step.TemperatureUnitId = null;
        step.TemperatureUnitDimension = null;
        step.Note = null;
        step.SortOrder = 11;

        var equipment = recipe.Equipment.Single();
        equipment.DisplayText = "a different tin";
        equipment.EquipmentTypeId = null;
        equipment.IsOptional = false;
        equipment.Note = null;
        equipment.SortOrder = 11;

        var link = recipe.AssetLinks.Single();
        link.MediaAssetId = Guid.NewGuid();
        link.Role = RecipeAssetRole.Gallery;
        link.Caption = null;
        link.SortOrder = 11;

        recipe.Tags.Clear();
        recipe.Tags.Add(new RecipeTag { WorkspaceId = recipe.WorkspaceId, RecipeId = recipe.Id, WorkspaceTagId = TagB });
    }

    // ---- Nothing to do ----

    /// <summary>
    /// A restore onto content that already matches changes nothing and says so. This is what makes the no-op
    /// rule above it safe: Business asks this question after reconciling, and a resubmitted restore must not
    /// write a second version.
    /// </summary>
    [Fact]
    public void Restoring_content_a_recipe_already_has_changes_nothing()
    {
        var (live, archived) = Archive();

        Assert.False(Apply(live, archived));
    }

    /// <summary>
    /// And it is genuinely idempotent: applying twice is applying once. Asserted separately from the
    /// round-trip test because "reports no change" and "makes no change" are different claims.
    /// </summary>
    [Fact]
    public void Restoring_twice_is_restoring_once()
    {
        var (live, archived) = Archive();

        Ruin(live);

        Assert.True(Apply(live, archived));
        Assert.False(Apply(live, archived));
    }

    // ---- The header ----

    [Fact]
    public void A_single_changed_header_field_is_restored()
    {
        var (live, archived) = Archive();
        var headnote = live.Headnote;
        live.Headnote = "Rewritten.";

        Assert.True(Apply(live, archived));
        Assert.Equal(headnote, live.Headnote);
    }

    /// <summary>
    /// The one editorial field in the header, restored like any other — a version captured while the recipe
    /// was Ready puts Ready back.
    /// </summary>
    [Fact]
    public void Status_is_restored()
    {
        var (live, archived) = Archive();
        live.Status = RecipeStatus.Draft;

        Assert.True(Apply(live, archived));
        Assert.Equal(RecipeStatus.Ready, live.Status);
    }

    /// <summary>
    /// Restoring cannot move a recipe into the archive, because no snapshot can carry that state — archiving
    /// writes no version and an archived recipe accepts no edits, so nothing ever captures one. That claim is
    /// about the write seams rather than about this method, so it is asserted where it becomes true:
    /// <c>RecipeArchiveEndpointTests</c> checks that archiving leaves the history untouched.
    /// </summary>

    /// <summary>
    /// The unit and its dimension are one composite foreign key, so they are restored together. Taking one
    /// from the archive and deriving the other is the one way to make that key pin a contradiction.
    /// </summary>
    [Fact]
    public void A_yield_unit_and_its_dimension_are_restored_together()
    {
        var (live, archived) = Archive();
        live.YieldUnitId = null;
        live.YieldUnitDimension = null;

        Assert.True(Apply(live, archived));
        Assert.Equal(RecipeAggregateFixture.EachId, live.YieldUnitId);
        Assert.Equal(MeasurementDimension.Count, live.YieldUnitDimension);
    }

    /// <summary>
    /// Identity, ownership, authorship, audit and lineage are not in the archive and must not move. A restore
    /// puts back what a recipe said, never who owns it, who wrote it, or what it is a copy of.
    /// </summary>
    [Fact]
    public void Identity_ownership_authorship_audit_and_lineage_are_untouched()
    {
        var (live, archived) = Archive();

        var (id, workspaceId) = (live.Id, live.WorkspaceId);
        var (createdBy, updatedBy) = (live.CreatedByMembershipId, live.UpdatedByMembershipId);
        var (createdAt, updatedAt) = (live.CreatedAt, live.UpdatedAt);
        var rowVersion = live.RowVersion;
        var duplicatedFrom = live.DuplicatedFromVersionId;

        Ruin(live);
        Apply(live, archived);

        Assert.Equal(id, live.Id);
        Assert.Equal(workspaceId, live.WorkspaceId);
        Assert.Equal(createdBy, live.CreatedByMembershipId);
        Assert.Equal(updatedBy, live.UpdatedByMembershipId);
        Assert.Equal(createdAt, live.CreatedAt);
        Assert.Equal(updatedAt, live.UpdatedAt);
        Assert.Same(rowVersion, live.RowVersion);

        // Restoring an old version of a copy does not make it a copy of something else.
        Assert.Equal(duplicatedFrom, live.DuplicatedFromVersionId);
    }

    // ---- Children ----

    [Fact]
    public void A_line_the_document_does_not_name_is_removed()
    {
        var (live, archived) = Archive();
        var group = live.IngredientGroups.Single();
        group.Ingredients.Add(new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            RecipeId = live.Id,
            RecipeIngredientGroupId = group.Id,
            SortOrder = 9,
            DisplayText = "added later",
        });

        Assert.True(Apply(live, archived));
        Assert.Single(live.IngredientGroups.Single().Ingredients);
    }

    /// <summary>
    /// A line the document names and the recipe does not have comes back — keeping its archived id, which is
    /// what lets a later diff match it to itself rather than reporting a deletion and an insertion.
    /// </summary>
    [Fact]
    public void A_line_the_recipe_no_longer_has_is_restored_with_its_own_id()
    {
        var (live, archived) = Archive();
        var archivedLine = archived.IngredientGroups.Single().Ingredients.Single();

        live.IngredientGroups.Single().Ingredients.Clear();

        Assert.True(Apply(live, archived));

        var restored = live.IngredientGroups.Single().Ingredients.Single();
        Assert.Equal(archivedLine.Id, restored.Id);
        Assert.Equal(archivedLine.DisplayText, restored.DisplayText);
        Assert.Equal(WorkspaceId, restored.WorkspaceId);
        Assert.Equal(live.IngredientGroups.Single().Id, restored.RecipeIngredientGroupId);
    }

    /// <summary>
    /// A group the recipe no longer has comes back whole — with its lines, which is the case a naive adoption
    /// of the document's own group object gets wrong by emptying the very collection it then reads.
    /// </summary>
    [Fact]
    public void A_group_the_recipe_no_longer_has_is_restored_with_its_children_and_its_owner()
    {
        var (live, archived) = Archive();
        live.IngredientGroups.Clear();
        live.InstructionGroups.Clear();

        Assert.True(Apply(live, archived));

        Assert.Equal(WorkspaceId, live.IngredientGroups.Single().WorkspaceId);
        Assert.Equal(WorkspaceId, live.InstructionGroups.Single().WorkspaceId);
        Assert.Equal(WorkspaceId, live.IngredientGroups.Single().Ingredients.Single().WorkspaceId);
        Assert.Equal(WorkspaceId, live.InstructionGroups.Single().Steps.Single().WorkspaceId);
        Assert.Equal(
            archived.IngredientGroups.Single().Ingredients.Single().DisplayText,
            live.IngredientGroups.Single().Ingredients.Single().DisplayText);
    }

    [Fact]
    public void A_group_the_document_does_not_name_is_removed()
    {
        var (live, archived) = Archive();
        live.IngredientGroups.Add(new RecipeIngredientGroup
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            RecipeId = live.Id,
            Title = "For the streusel",
            SortOrder = 9,
        });

        Assert.True(Apply(live, archived));
        Assert.Single(live.IngredientGroups);
    }

    /// <summary>
    /// The case the reconciler exists for. A step whose group changed is <em>moved</em> — the same instance,
    /// reparented — because removing it and inserting the document's copy would put two entities with one key
    /// in the change tracker and imply a delete-then-insert of the same id.
    /// </summary>
    [Fact]
    public void A_step_that_moved_groups_is_reparented_rather_than_recreated()
    {
        var live = Live();
        var original = live.InstructionGroups.Single();
        var step = original.Steps.Single();

        var second = new RecipeInstructionGroup
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            RecipeId = live.Id,
            Title = "To finish",

            // Above the populated group's 3, so the document's ordering is unambiguous below.
            SortOrder = 4,
        };
        live.InstructionGroups.Add(second);

        // The archive, with the step moved across in the document rather than in the aggregate — which is
        // exactly what a creator who reordered their method and then restored would have written.
        var captured = Capture(live);
        var first = captured.InstructionGroups[0];
        var last = captured.InstructionGroups[1];
        var archived = captured with
        {
            InstructionGroups = [first with { Steps = [] }, last with { Steps = first.Steps }],
        };

        Assert.True(Apply(live, archived));

        var moved = live.InstructionGroups.Single(group => group.Id == second.Id).Steps.Single();
        Assert.Same(step, moved);
        Assert.Equal(second.Id, moved.RecipeInstructionGroupId);
        Assert.Empty(live.InstructionGroups.Single(group => group.Id == original.Id).Steps);
    }

    [Fact]
    public void Equipment_and_asset_links_are_reconciled()
    {
        var (live, archived) = Archive();
        var caption = live.AssetLinks.Single().Caption;

        live.Equipment.Clear();
        live.AssetLinks.Single().Caption = "Rewritten.";

        Assert.True(Apply(live, archived));
        Assert.Single(live.Equipment);
        Assert.Equal(WorkspaceId, live.Equipment.Single().WorkspaceId);
        Assert.Equal(caption, live.AssetLinks.Single().Caption);
    }

    // ---- Tags ----

    [Fact]
    public void The_archived_tags_replace_the_recipes_tags()
    {
        var (live, archived) = Archive();
        live.Tags.Clear();
        live.Tags.Add(new RecipeTag { WorkspaceId = WorkspaceId, RecipeId = live.Id, WorkspaceTagId = TagB });

        Assert.True(Apply(live, archived));
        Assert.Equal([TagA], live.Tags.Select(tag => tag.WorkspaceTagId));
    }

    /// <summary>
    /// A tag whose vocabulary row is gone is dropped rather than sent to a <c>Restrict</c> foreign key to
    /// fail. Unreachable today — nothing deletes a <c>WorkspaceTag</c> — and handled because failing loudly
    /// at the database would turn a creator's restore into a 500 over a tag.
    /// </summary>
    [Fact]
    public void A_tag_no_longer_in_the_vocabulary_is_dropped()
    {
        var (live, archived) = Archive();

        Assert.True(RecipeSnapshotReconciler.Apply(live, archived, []));
        Assert.Empty(live.Tags);
    }

    /// <summary>
    /// And dropping it stays a no-op on the next restore, rather than reporting a change every time and
    /// writing a version per attempt.
    /// </summary>
    [Fact]
    public void Dropping_a_vanished_tag_does_not_repeat()
    {
        var (live, archived) = Archive();

        Assert.True(RecipeSnapshotReconciler.Apply(live, archived, []));
        Assert.False(RecipeSnapshotReconciler.Apply(live, archived, []));
        Assert.Empty(live.Tags);
    }

    // ---- Documents this build cannot read ----

    /// <summary>
    /// Refused before anything is touched, so a restore that cannot be performed leaves no half-applied
    /// aggregate behind.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(RecipeSnapshotDocument.CurrentSchemaVersion + 1)]
    public void An_unreadable_document_is_refused_without_changing_anything(int schemaVersion)
    {
        var (live, archived) = Archive();
        var title = live.Title;

        var unreadable = archived with
        {
            SchemaVersion = schemaVersion,
            Recipe = new RecipeSnapshotHeader { Title = "Should never be applied" },
        };

        Assert.Throws<NotSupportedException>(() => Apply(live, unreadable));
        Assert.Equal(title, live.Title);
    }
}
