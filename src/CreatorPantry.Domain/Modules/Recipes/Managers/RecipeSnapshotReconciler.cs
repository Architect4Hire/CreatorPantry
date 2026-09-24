using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Makes a live recipe aggregate say exactly what a snapshot document says, in place, and reports whether
/// anything actually changed. Pure: no persistence, no clock, no workspace, no identity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not <see cref="RecipeSnapshotMapper.Restore"/>.</strong> That method projects a
/// document into a <em>detached</em> graph, which is the right answer for a caller that wants to read a
/// version's content and the wrong one for a restore: overwriting the live aggregate with a detached copy
/// would delete every child and insert a new one with the same id, which EF cannot express in one batch and
/// which would destroy the stable identities a later diff matches on. So this reconciles — a child named by
/// the document is updated where it sits, one the document does not name is removed, and one the recipe does
/// not yet have is added.
/// </para>
/// <para>
/// <strong>It builds the target graph with the mapper rather than reading the document itself.</strong> The
/// document-to-entity field mapping then exists in exactly one place; a field added to the archive and
/// forgotten here would be forgotten in one file instead of two, and
/// <c>RecipeSnapshotCompletenessTests</c> already watches that one. What remains here is entity-to-entity
/// copying and placement.
/// </para>
/// <para>
/// <strong>Children move between groups without being recreated.</strong> An ingredient or step whose group
/// changed is the same row with a new parent, so its live instance is moved and its foreign key reassigned.
/// Removing it from one group and inserting the document's copy into another would put two entities with one
/// key in the change tracker, and the delete-then-insert it implies is the failure this whole method exists
/// to avoid.
/// </para>
/// <para>
/// <strong>The document is untrusted input</strong> — <see cref="RecipeSnapshotMapper"/> says why, and
/// nothing here revalidates the ids it carries. The caller is responsible for having read it from the
/// resolved workspace. The one exception is the tag vocabulary, which has a real foreign key with
/// <c>Restrict</c> behind it: <c>availableTags</c> is how the caller states which of the tag ids the
/// document names actually exist here, and an id absent from it is dropped rather than sent to the database
/// to fail.
/// </para>
/// <para>
/// <strong><c>WorkspaceId</c> is set explicitly on every entity added here</strong>, and not left for
/// <c>WorkspaceOwnershipInterceptor</c> — the reason <c>RecipeBusiness.BuildInstructionGroup</c> records at
/// length: the interceptor runs at <c>SavingChanges</c>, after EF's relationship fixup has already tried to
/// propagate a value onto a new child of an already-loaded parent, and <c>WorkspaceId</c> sits in the
/// alternate keys that the groups' children point at. Copied from the recipe, which is a tracked row whose
/// own ownership was settled long ago, so this carries the resolved workspace rather than choosing one.
/// </para>
/// </remarks>
public static class RecipeSnapshotReconciler
{
    /// <summary>
    /// Applies <paramref name="document"/> onto <paramref name="recipe"/>.
    /// </summary>
    /// <param name="recipe">
    /// A tracked aggregate loaded whole, with every child collection populated. A recipe loaded without its
    /// <c>Include</c>s would appear to have no children, and this would dutifully insert the document's —
    /// duplicating every row the recipe already had.
    /// </param>
    /// <param name="document">The archived content to put back.</param>
    /// <param name="availableTags">
    /// The workspace's vocabulary rows for the tag ids <paramref name="document"/> names. Only the ids are
    /// read. An empty collection is a legitimate answer and means no tag the document names still exists.
    /// </param>
    /// <returns>
    /// Whether anything about the recipe changed. <c>false</c> means the recipe already said exactly what the
    /// document says, which is a restore with nothing to record.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The document carries no schema version, or one this build cannot read.
    /// </exception>
    public static bool Apply(
        Recipe recipe,
        RecipeSnapshotDocument document,
        IReadOnlyCollection<WorkspaceTag> availableTags)
    {
        // Raises on an unreadable document before anything is touched, so a refused restore cannot leave a
        // half-applied aggregate behind. RecipeSnapshotMapper.Restore performs the check itself; calling it
        // first is what makes "nothing was touched" true rather than nearly true.
        document.EnsureReadable();

        var target = RecipeSnapshotMapper.Restore(document, recipe.Id);

        // Every clause runs. Not short-circuited with ||, deliberately: each of these reconciles a collection
        // as a side effect, and the first one to report a change would stop the rest from happening at all.
        var changed = ApplyHeader(recipe, target);
        changed |= ReconcileIngredients(recipe, target);
        changed |= ReconcileInstructions(recipe, target);
        changed |= ReconcileEquipment(recipe, target);
        changed |= ReconcileAssetLinks(recipe, target);
        changed |= ReconcileTags(recipe, target, availableTags);

        return changed;
    }

    /// <summary>
    /// Copies the recipe's own content fields, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Identity, ownership, authorship, audit times and the concurrency token are all absent, because
    /// <see cref="RecipeSnapshotHeader"/> does not carry them — a restore puts back what a recipe said, never
    /// who owns it or who wrote it. <c>RecipeSnapshotCompletenessTests</c> is what keeps that list honest in
    /// both directions.
    /// </para>
    /// <para>
    /// <see cref="Recipe.Status"/> <strong>is</strong> restored, which is worth stating because it is the one
    /// field here that is editorial rather than content. It keeps the version a restore writes identical to
    /// the version it restored, which is the property a later diff of the two depends on.
    /// </para>
    /// <para>
    /// <strong>It cannot move a recipe into or out of the archive, and that is structural rather than
    /// checked here.</strong> Out of, because an archived recipe refuses a restore altogether
    /// (<c>RecipePolicy.AcceptsContentChanges</c>). Into, because no snapshot can carry
    /// <c>RecipeStatus.Archived</c> in the first place: archiving writes no version, and a recipe cannot be
    /// edited while archived, so no capture ever sees that state. Between them, the lifecycle stays
    /// reachable only through the audited commands — see <c>IRecipeDataLayer.TrySetStatusAsync</c>.
    /// </para>
    /// <para>
    /// <see cref="Recipe.YieldUnitDimension"/> is copied rather than re-derived, unlike on the create and
    /// update paths where it is resolved from the submitted unit. It is not a submitted value here: the
    /// archive recorded the dimension the unit had when the version was written, and the two travel together
    /// in the composite foreign key, so taking the unit from the document and the dimension from elsewhere is
    /// the one way to make that key pin a contradiction.
    /// </para>
    /// </remarks>
    private static bool ApplyHeader(Recipe recipe, Recipe target)
    {
        var changed = recipe.Title != target.Title
            || recipe.Description != target.Description
            || recipe.Headnote != target.Headnote
            || recipe.Notes != target.Notes
            || recipe.StorageNotes != target.StorageNotes
            || recipe.AttributionText != target.AttributionText
            || recipe.SourceUrl != target.SourceUrl
            || recipe.CuisineId != target.CuisineId
            || recipe.CourseId != target.CourseId
            || recipe.PrimaryTechniqueId != target.PrimaryTechniqueId
            || recipe.PrepTimeMinutes != target.PrepTimeMinutes
            || recipe.CookTimeMinutes != target.CookTimeMinutes
            || recipe.RestTimeMinutes != target.RestTimeMinutes
            || recipe.TotalTimeMinutes != target.TotalTimeMinutes
            || recipe.YieldText != target.YieldText
            || recipe.YieldQuantity != target.YieldQuantity
            || recipe.YieldUnitId != target.YieldUnitId
            || recipe.YieldUnitDimension != target.YieldUnitDimension
            || recipe.Status != target.Status;

        if (!changed)
        {
            return false;
        }

        recipe.Title = target.Title;
        recipe.Description = target.Description;
        recipe.Headnote = target.Headnote;
        recipe.Notes = target.Notes;
        recipe.StorageNotes = target.StorageNotes;
        recipe.AttributionText = target.AttributionText;
        recipe.SourceUrl = target.SourceUrl;
        recipe.CuisineId = target.CuisineId;
        recipe.CourseId = target.CourseId;
        recipe.PrimaryTechniqueId = target.PrimaryTechniqueId;
        recipe.PrepTimeMinutes = target.PrepTimeMinutes;
        recipe.CookTimeMinutes = target.CookTimeMinutes;
        recipe.RestTimeMinutes = target.RestTimeMinutes;
        recipe.TotalTimeMinutes = target.TotalTimeMinutes;
        recipe.YieldText = target.YieldText;
        recipe.YieldQuantity = target.YieldQuantity;
        recipe.YieldUnitId = target.YieldUnitId;
        recipe.YieldUnitDimension = target.YieldUnitDimension;
        recipe.Status = target.Status;

        return true;
    }

    /// <summary>
    /// Reconciles the ingredient groups and, across all of them at once, the lines they hold.
    /// </summary>
    /// <remarks>
    /// The lines are placed in a pass of their own rather than group by group, because a line's group is
    /// something a restore can change: reconciling within each group would see the same line as removed from
    /// one and added to another. One map of every live line, keyed by id, is what makes a move a move.
    /// </remarks>
    private static bool ReconcileIngredients(Recipe recipe, Recipe target)
    {
        var live = recipe.IngredientGroups.ToDictionary(group => group.Id);
        var liveLines = recipe.IngredientGroups
            .SelectMany(group => group.Ingredients)
            .ToDictionary(line => line.Id);

        var changed = false;
        var keptGroups = new HashSet<Guid>();
        var keptLines = new HashSet<Guid>();

        foreach (var wanted in target.IngredientGroups)
        {
            // Read out before the group below is adopted, because adopting it means clearing this very
            // collection — the target group and the added group are then one object, and iterating what was
            // just emptied would silently restore a group with no lines in it.
            var wantedLines = wanted.Ingredients.ToList();

            RecipeIngredientGroup group;

            if (live.TryGetValue(wanted.Id, out var found))
            {
                group = found;

                if (group.Title != wanted.Title || group.SortOrder != wanted.SortOrder)
                {
                    group.Title = wanted.Title;
                    group.SortOrder = wanted.SortOrder;
                    changed = true;
                }
            }
            else
            {
                // The document's own instance, adopted rather than copied — it already carries the archived
                // id, title and order. Emptied first: its lines are placed by the pass below, which is the
                // only code that can tell a new line from one moving in from another group.
                group = wanted;
                group.WorkspaceId = recipe.WorkspaceId;
                group.Ingredients.Clear();
                recipe.IngredientGroups.Add(group);
                live[group.Id] = group;
                changed = true;
            }

            keptGroups.Add(group.Id);

            foreach (var line in wantedLines)
            {
                keptLines.Add(line.Id);

                if (!liveLines.TryGetValue(line.Id, out var existing))
                {
                    line.WorkspaceId = recipe.WorkspaceId;
                    line.RecipeIngredientGroupId = group.Id;
                    group.Ingredients.Add(line);
                    changed = true;

                    continue;
                }

                if (existing.RecipeIngredientGroupId != group.Id)
                {
                    // A move: the same row, reparented. The instance leaves one collection and joins another,
                    // so EF updates its foreign key instead of deleting and re-inserting the id.
                    live[existing.RecipeIngredientGroupId].Ingredients.Remove(existing);
                    existing.RecipeIngredientGroupId = group.Id;
                    group.Ingredients.Add(existing);
                    changed = true;
                }

                changed |= Copy(existing, line);
            }
        }

        foreach (var orphan in liveLines.Values.Where(line => !keptLines.Contains(line.Id)).ToList())
        {
            live[orphan.RecipeIngredientGroupId].Ingredients.Remove(orphan);
            changed = true;
        }

        foreach (var orphan in live.Values.Where(group => !keptGroups.Contains(group.Id)).ToList())
        {
            recipe.IngredientGroups.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <inheritdoc cref="ReconcileIngredients"/>
    private static bool ReconcileInstructions(Recipe recipe, Recipe target)
    {
        var live = recipe.InstructionGroups.ToDictionary(group => group.Id);
        var liveSteps = recipe.InstructionGroups
            .SelectMany(group => group.Steps)
            .ToDictionary(step => step.Id);

        var changed = false;
        var keptGroups = new HashSet<Guid>();
        var keptSteps = new HashSet<Guid>();

        foreach (var wanted in target.InstructionGroups)
        {
            // Read out before adoption, for the reason ReconcileIngredients gives.
            var wantedSteps = wanted.Steps.ToList();

            RecipeInstructionGroup group;

            if (live.TryGetValue(wanted.Id, out var found))
            {
                group = found;

                if (group.Title != wanted.Title || group.SortOrder != wanted.SortOrder)
                {
                    group.Title = wanted.Title;
                    group.SortOrder = wanted.SortOrder;
                    changed = true;
                }
            }
            else
            {
                group = wanted;
                group.WorkspaceId = recipe.WorkspaceId;
                group.Steps.Clear();
                recipe.InstructionGroups.Add(group);
                live[group.Id] = group;
                changed = true;
            }

            keptGroups.Add(group.Id);

            foreach (var step in wantedSteps)
            {
                keptSteps.Add(step.Id);

                if (!liveSteps.TryGetValue(step.Id, out var existing))
                {
                    step.WorkspaceId = recipe.WorkspaceId;
                    step.RecipeInstructionGroupId = group.Id;
                    group.Steps.Add(step);
                    changed = true;

                    continue;
                }

                if (existing.RecipeInstructionGroupId != group.Id)
                {
                    live[existing.RecipeInstructionGroupId].Steps.Remove(existing);
                    existing.RecipeInstructionGroupId = group.Id;
                    group.Steps.Add(existing);
                    changed = true;
                }

                changed |= Copy(existing, step);
            }
        }

        foreach (var orphan in liveSteps.Values.Where(step => !keptSteps.Contains(step.Id)).ToList())
        {
            live[orphan.RecipeInstructionGroupId].Steps.Remove(orphan);
            changed = true;
        }

        foreach (var orphan in live.Values.Where(group => !keptGroups.Contains(group.Id)).ToList())
        {
            recipe.InstructionGroups.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    private static bool ReconcileEquipment(Recipe recipe, Recipe target)
    {
        var live = recipe.Equipment.ToDictionary(item => item.Id);
        var changed = false;
        var kept = new HashSet<Guid>();

        foreach (var wanted in target.Equipment)
        {
            kept.Add(wanted.Id);

            if (live.TryGetValue(wanted.Id, out var existing))
            {
                changed |= Copy(existing, wanted);
            }
            else
            {
                wanted.WorkspaceId = recipe.WorkspaceId;
                recipe.Equipment.Add(wanted);
                changed = true;
            }
        }

        foreach (var orphan in live.Values.Where(item => !kept.Contains(item.Id)).ToList())
        {
            recipe.Equipment.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <remarks>
    /// <see cref="RecipeAssetLink.MediaAssetId"/> is put back exactly as archived, and there is no foreign key
    /// behind it to catch an id that has since been deleted — the media aggregate does not exist yet, and both
    /// <see cref="RecipeSnapshotAssetLink"/> and the entity itself record that the write seam owes this a
    /// workspace check once it does. Today the only documents reachable here came from this workspace's own
    /// versions, so every id in one was this workspace's when it was written.
    /// </remarks>
    private static bool ReconcileAssetLinks(Recipe recipe, Recipe target)
    {
        var live = recipe.AssetLinks.ToDictionary(link => link.Id);
        var changed = false;
        var kept = new HashSet<Guid>();

        foreach (var wanted in target.AssetLinks)
        {
            kept.Add(wanted.Id);

            if (live.TryGetValue(wanted.Id, out var existing))
            {
                changed |= Copy(existing, wanted);
            }
            else
            {
                wanted.WorkspaceId = recipe.WorkspaceId;
                recipe.AssetLinks.Add(wanted);
                changed = true;
            }
        }

        foreach (var orphan in live.Values.Where(link => !kept.Contains(link.Id)).ToList())
        {
            recipe.AssetLinks.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Makes the recipe's tag links name exactly the tags the document names that still exist here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A link is identity and nothing else — <c>(WorkspaceId, RecipeId, WorkspaceTagId)</c> is its whole
    /// primary key — so there is no field to copy and reconciling is set arithmetic.
    /// </para>
    /// <para>
    /// A tag the document names that is no longer in the vocabulary is dropped, silently and by design. The
    /// alternative is an insert the <c>Restrict</c> foreign key refuses, which would turn a creator's restore
    /// into a 500 over a tag. Nothing deletes a <c>WorkspaceTag</c> today — dropping the last link to one
    /// deliberately leaves the vocabulary row alone — so this is unreachable rather than merely unlikely; it
    /// is handled because the day it becomes reachable, failing loudly at the database is the worse answer.
    /// The drop is idempotent: a second restore of the same version finds the links already matching and
    /// reports no change, rather than writing a version every time.
    /// </para>
    /// </remarks>
    private static bool ReconcileTags(
        Recipe recipe, Recipe target, IReadOnlyCollection<WorkspaceTag> availableTags)
    {
        var available = availableTags.Select(tag => tag.Id).ToHashSet();
        var wanted = target.Tags
            .Select(tag => tag.WorkspaceTagId)
            .Where(available.Contains)
            .ToHashSet();

        var changed = false;

        foreach (var orphan in recipe.Tags.Where(link => !wanted.Contains(link.WorkspaceTagId)).ToList())
        {
            recipe.Tags.Remove(orphan);
            changed = true;
        }

        var held = recipe.Tags.Select(link => link.WorkspaceTagId).ToHashSet();

        foreach (var tagId in wanted.Where(id => !held.Contains(id)))
        {
            // WorkspaceId is left for the interceptor here, unlike every other entity added above, and
            // RecipeDataLayer.AttachTagsAsync does the same: a link is a dependent on both sides and is the
            // principal of nothing, so no alternate key is waiting on the value at fixup time.
            recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = tagId });
            changed = true;
        }

        return changed;
    }

    private static bool Copy(RecipeIngredient live, RecipeIngredient wanted)
    {
        var changed = live.SortOrder != wanted.SortOrder
            || live.DisplayText != wanted.DisplayText
            || live.IngredientNameText != wanted.IngredientNameText
            || live.Quantity != wanted.Quantity
            || live.QuantityUpper != wanted.QuantityUpper
            || live.MeasurementUnitId != wanted.MeasurementUnitId
            || live.MeasurementUnitDimension != wanted.MeasurementUnitDimension
            || live.IngredientId != wanted.IngredientId
            || live.MatchStatus != wanted.MatchStatus
            || live.PreparationNote != wanted.PreparationNote
            || live.IsOptional != wanted.IsOptional
            || live.ScalingBehavior != wanted.ScalingBehavior;

        if (!changed)
        {
            return false;
        }

        live.SortOrder = wanted.SortOrder;
        live.DisplayText = wanted.DisplayText;
        live.IngredientNameText = wanted.IngredientNameText;
        live.Quantity = wanted.Quantity;
        live.QuantityUpper = wanted.QuantityUpper;
        live.MeasurementUnitId = wanted.MeasurementUnitId;
        live.MeasurementUnitDimension = wanted.MeasurementUnitDimension;
        live.IngredientId = wanted.IngredientId;
        live.MatchStatus = wanted.MatchStatus;
        live.PreparationNote = wanted.PreparationNote;
        live.IsOptional = wanted.IsOptional;
        live.ScalingBehavior = wanted.ScalingBehavior;

        return true;
    }

    /// <remarks>
    /// <see cref="RecipeInstructionStep.TemperatureUnitDimension"/> is copied rather than derived, for the
    /// reason <see cref="ApplyHeader"/> gives about the yield unit: nothing was submitted here, and the unit
    /// and its dimension are one composite foreign key that must agree with itself.
    /// </remarks>
    private static bool Copy(RecipeInstructionStep live, RecipeInstructionStep wanted)
    {
        var changed = live.SortOrder != wanted.SortOrder
            || live.Text != wanted.Text
            || live.TechniqueId != wanted.TechniqueId
            || live.DurationMinutes != wanted.DurationMinutes
            || live.TemperatureValue != wanted.TemperatureValue
            || live.TemperatureUnitId != wanted.TemperatureUnitId
            || live.TemperatureUnitDimension != wanted.TemperatureUnitDimension
            || live.Note != wanted.Note;

        if (!changed)
        {
            return false;
        }

        live.SortOrder = wanted.SortOrder;
        live.Text = wanted.Text;
        live.TechniqueId = wanted.TechniqueId;
        live.DurationMinutes = wanted.DurationMinutes;
        live.TemperatureValue = wanted.TemperatureValue;
        live.TemperatureUnitId = wanted.TemperatureUnitId;
        live.TemperatureUnitDimension = wanted.TemperatureUnitDimension;
        live.Note = wanted.Note;

        return true;
    }

    private static bool Copy(RecipeEquipment live, RecipeEquipment wanted)
    {
        var changed = live.SortOrder != wanted.SortOrder
            || live.DisplayText != wanted.DisplayText
            || live.EquipmentTypeId != wanted.EquipmentTypeId
            || live.IsOptional != wanted.IsOptional
            || live.Note != wanted.Note;

        if (!changed)
        {
            return false;
        }

        live.SortOrder = wanted.SortOrder;
        live.DisplayText = wanted.DisplayText;
        live.EquipmentTypeId = wanted.EquipmentTypeId;
        live.IsOptional = wanted.IsOptional;
        live.Note = wanted.Note;

        return true;
    }

    private static bool Copy(RecipeAssetLink live, RecipeAssetLink wanted)
    {
        var changed = live.SortOrder != wanted.SortOrder
            || live.MediaAssetId != wanted.MediaAssetId
            || live.Role != wanted.Role
            || live.Caption != wanted.Caption;

        if (!changed)
        {
            return false;
        }

        live.SortOrder = wanted.SortOrder;
        live.MediaAssetId = wanted.MediaAssetId;
        live.Role = wanted.Role;
        live.Caption = wanted.Caption;

        return true;
    }
}
