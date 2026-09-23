using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Translates a live recipe aggregate into a snapshot document and back. Pure and total: no persistence, no
/// clock, no workspace, no identity — give it the same aggregate twice and it produces the same document.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Capture"/> takes a <see cref="CompleteRecipe"/> rather than a <see cref="Recipe"/>, and that
/// is the whole reason that type exists. It reads the child collections directly, so a recipe loaded without
/// its <c>Include</c>s would be snapshotted as an empty one — silently, permanently, into a row that cannot
/// be corrected. Requiring the loaded-aggregate type means the mistake takes a deliberate act rather than a
/// forgotten <c>Include</c>.
/// </para>
/// <para>
/// <see cref="Restore"/> returns a detached child graph rather than mutating anything. Deciding what to do
/// with it — overwrite the live recipe, open a proposal, write a new version — is a domain decision that
/// belongs to Business, not to a mapper. Restoring content is never the same act as restoring ownership:
/// the document carries no workspace, no author and no timestamps, so nothing here can move a recipe
/// between workspaces or rewrite who wrote it.
/// </para>
/// <para>
/// <strong>A document is untrusted input.</strong> Today the only way to obtain one is through the
/// workspace-filtered snapshot set, so scope is enforced by the read that precedes this — but that is an
/// implicit control, and it stops holding the moment a document arrives from an import
/// (<see cref="RecipeVersionSource.Import"/>) or any other origin. <see cref="Restore"/> validates the
/// schema version and nothing else: it does not and cannot check the ids inside. The caller is responsible
/// for having read the document from its own workspace, and for revalidating the workspace-owned ids it
/// carries — <see cref="RecipeSnapshotAssetLink.MediaAssetId"/> above all — before anything is persisted.
/// </para>
/// </remarks>
public static class RecipeSnapshotMapper
{
    /// <summary>Captures the complete content of a loaded recipe aggregate.</summary>
    /// <param name="loaded">
    /// An aggregate a repository loaded whole. Only its <see cref="CompleteRecipe.Recipe"/> is read — the
    /// version metadata beside it describes the history, not the content being archived.
    /// </param>
    public static RecipeSnapshotDocument Capture(CompleteRecipe loaded) => CaptureContent(loaded.Recipe);

    private static RecipeSnapshotDocument CaptureContent(Recipe recipe) => new()
    {
        SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        Recipe = new RecipeSnapshotHeader
        {
            Title = recipe.Title,
            Description = recipe.Description,
            Headnote = recipe.Headnote,
            Notes = recipe.Notes,
            StorageNotes = recipe.StorageNotes,
            AttributionText = recipe.AttributionText,
            SourceUrl = recipe.SourceUrl,
            CuisineId = recipe.CuisineId,
            CourseId = recipe.CourseId,
            PrimaryTechniqueId = recipe.PrimaryTechniqueId,
            PrepTimeMinutes = recipe.PrepTimeMinutes,
            CookTimeMinutes = recipe.CookTimeMinutes,
            RestTimeMinutes = recipe.RestTimeMinutes,
            TotalTimeMinutes = recipe.TotalTimeMinutes,
            YieldText = recipe.YieldText,
            YieldQuantity = recipe.YieldQuantity,
            YieldUnitId = recipe.YieldUnitId,
            YieldUnitDimension = recipe.YieldUnitDimension,
            Status = recipe.Status,
        },

        // Ordered by SortOrder rather than by whatever order EF materialised the collection in, so two
        // captures of the same recipe are byte-identical and a diff of an unchanged recipe is empty.
        IngredientGroups = [.. recipe.IngredientGroups.OrderBy(group => group.SortOrder).Select(group =>
            new RecipeSnapshotIngredientGroup
            {
                Id = group.Id,
                Title = group.Title,
                SortOrder = group.SortOrder,
                Ingredients = [.. group.Ingredients.OrderBy(line => line.SortOrder).Select(line =>
                    new RecipeSnapshotIngredient
                    {
                        Id = line.Id,
                        SortOrder = line.SortOrder,
                        DisplayText = line.DisplayText,
                        IngredientNameText = line.IngredientNameText,
                        Quantity = line.Quantity,
                        QuantityUpper = line.QuantityUpper,
                        MeasurementUnitId = line.MeasurementUnitId,
                        MeasurementUnitDimension = line.MeasurementUnitDimension,
                        IngredientId = line.IngredientId,
                        MatchStatus = line.MatchStatus,
                        PreparationNote = line.PreparationNote,
                        IsOptional = line.IsOptional,
                        ScalingBehavior = line.ScalingBehavior,
                    })],
            })],

        InstructionGroups = [.. recipe.InstructionGroups.OrderBy(group => group.SortOrder).Select(group =>
            new RecipeSnapshotInstructionGroup
            {
                Id = group.Id,
                Title = group.Title,
                SortOrder = group.SortOrder,
                Steps = [.. group.Steps.OrderBy(step => step.SortOrder).Select(step =>
                    new RecipeSnapshotInstructionStep
                    {
                        Id = step.Id,
                        SortOrder = step.SortOrder,
                        Text = step.Text,
                        TechniqueId = step.TechniqueId,
                        DurationMinutes = step.DurationMinutes,
                        TemperatureValue = step.TemperatureValue,
                        TemperatureUnitId = step.TemperatureUnitId,
                        TemperatureUnitDimension = step.TemperatureUnitDimension,
                        Note = step.Note,
                    })],
            })],

        Equipment = [.. recipe.Equipment.OrderBy(item => item.SortOrder).Select(item =>
            new RecipeSnapshotEquipment
            {
                Id = item.Id,
                SortOrder = item.SortOrder,
                DisplayText = item.DisplayText,
                EquipmentTypeId = item.EquipmentTypeId,
                IsOptional = item.IsOptional,
                Note = item.Note,
            })],

        AssetLinks = [.. recipe.AssetLinks.OrderBy(link => link.SortOrder).Select(link =>
            new RecipeSnapshotAssetLink
            {
                Id = link.Id,
                SortOrder = link.SortOrder,
                MediaAssetId = link.MediaAssetId,
                Role = link.Role,
                Caption = link.Caption,
            })],

        // Tags are a set with no order of their own, so they are ordered by id purely to make two captures of
        // the same recipe byte-identical — the property a diff depends on.
        Tags = [.. recipe.Tags.OrderBy(tag => tag.WorkspaceTagId).Select(tag =>
            new RecipeSnapshotTag { WorkspaceTagId = tag.WorkspaceTagId })],
    };

    /// <summary>
    /// Projects a snapshot back into a detached recipe aggregate carrying the supplied identity.
    /// </summary>
    /// <param name="document">A snapshot whose <see cref="RecipeSnapshotDocument.SchemaVersion"/> this code understands.</param>
    /// <param name="recipeId">
    /// The recipe the restored content belongs to. Supplied by the caller rather than read from the
    /// document, because a snapshot is content and says nothing about which row it is being applied to.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// The document carries no schema version, or one this build cannot read.
    /// </exception>
    public static Recipe Restore(RecipeSnapshotDocument document, Guid recipeId)
    {
        // Older documents must stay readable. Refusing anything but the current version would mean that the
        // day this constant is incremented, every snapshot already written in every workspace goes dark —
        // no restore, no diff, no history — which is the exact outcome choosing a self-describing document
        // over shadow tables was meant to prevent. Only the unreadable ends are refused: a document with no
        // version at all (0, so truncated rows and foreign JSON cannot pass as current), and one from a
        // future build whose meaning this code genuinely does not know.
        if (document.SchemaVersion < RecipeSnapshotDocument.MinimumReadableSchemaVersion
            || document.SchemaVersion > RecipeSnapshotDocument.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Recipe snapshot schema version {document.SchemaVersion} cannot be read by this build, which "
                    + $"reads versions {RecipeSnapshotDocument.MinimumReadableSchemaVersion} through "
                    + $"{RecipeSnapshotDocument.CurrentSchemaVersion}.");
        }

        // Only one shape exists so far, so there is nothing to upgrade between. When CurrentSchemaVersion
        // becomes 2, this is where a v1 document is brought forward — and a pinned literal v1 document,
        // checked into the tests rather than regenerated from this code, is what proves it still works.

        var header = document.Recipe;
        var recipe = new Recipe
        {
            Id = recipeId,
            Title = header.Title,
            Description = header.Description,
            Headnote = header.Headnote,
            Notes = header.Notes,
            StorageNotes = header.StorageNotes,
            AttributionText = header.AttributionText,
            SourceUrl = header.SourceUrl,
            CuisineId = header.CuisineId,
            CourseId = header.CourseId,
            PrimaryTechniqueId = header.PrimaryTechniqueId,
            PrepTimeMinutes = header.PrepTimeMinutes,
            CookTimeMinutes = header.CookTimeMinutes,
            RestTimeMinutes = header.RestTimeMinutes,
            TotalTimeMinutes = header.TotalTimeMinutes,
            YieldText = header.YieldText,
            YieldQuantity = header.YieldQuantity,
            YieldUnitId = header.YieldUnitId,
            YieldUnitDimension = header.YieldUnitDimension,
            Status = header.Status,
        };

        // WorkspaceId is left unset throughout, on every entity below as well: ownership comes from the
        // resolved context by way of WorkspaceOwnershipInterceptor, and a restore that carried a workspace
        // would be a restore that could move a recipe into one.
        foreach (var group in document.IngredientGroups ?? [])
        {
            var restored = new RecipeIngredientGroup
            {
                Id = group.Id,
                RecipeId = recipeId,
                Title = group.Title,
                SortOrder = group.SortOrder,
            };

            foreach (var line in group.Ingredients ?? [])
            {
                restored.Ingredients.Add(new RecipeIngredient
                {
                    Id = line.Id,
                    RecipeId = recipeId,
                    RecipeIngredientGroupId = group.Id,
                    SortOrder = line.SortOrder,
                    DisplayText = line.DisplayText,
                    IngredientNameText = line.IngredientNameText,
                    Quantity = line.Quantity,
                    QuantityUpper = line.QuantityUpper,
                    MeasurementUnitId = line.MeasurementUnitId,
                    MeasurementUnitDimension = line.MeasurementUnitDimension,
                    IngredientId = line.IngredientId,
                    MatchStatus = line.MatchStatus,
                    PreparationNote = line.PreparationNote,
                    IsOptional = line.IsOptional,
                    ScalingBehavior = line.ScalingBehavior,
                });
            }

            recipe.IngredientGroups.Add(restored);
        }

        foreach (var group in document.InstructionGroups ?? [])
        {
            var restored = new RecipeInstructionGroup
            {
                Id = group.Id,
                RecipeId = recipeId,
                Title = group.Title,
                SortOrder = group.SortOrder,
            };

            foreach (var step in group.Steps ?? [])
            {
                restored.Steps.Add(new RecipeInstructionStep
                {
                    Id = step.Id,
                    RecipeId = recipeId,
                    RecipeInstructionGroupId = group.Id,
                    SortOrder = step.SortOrder,
                    Text = step.Text,
                    TechniqueId = step.TechniqueId,
                    DurationMinutes = step.DurationMinutes,
                    TemperatureValue = step.TemperatureValue,
                    TemperatureUnitId = step.TemperatureUnitId,
                    TemperatureUnitDimension = step.TemperatureUnitDimension,
                    Note = step.Note,
                });
            }

            recipe.InstructionGroups.Add(restored);
        }

        foreach (var item in document.Equipment ?? [])
        {
            recipe.Equipment.Add(new RecipeEquipment
            {
                Id = item.Id,
                RecipeId = recipeId,
                SortOrder = item.SortOrder,
                DisplayText = item.DisplayText,
                EquipmentTypeId = item.EquipmentTypeId,
                IsOptional = item.IsOptional,
                Note = item.Note,
            });
        }

        foreach (var link in document.AssetLinks ?? [])
        {
            recipe.AssetLinks.Add(new RecipeAssetLink
            {
                Id = link.Id,
                RecipeId = recipeId,
                SortOrder = link.SortOrder,
                MediaAssetId = link.MediaAssetId,
                Role = link.Role,
                Caption = link.Caption,
            });
        }

        foreach (var tag in document.Tags ?? [])
        {
            recipe.Tags.Add(new RecipeTag
            {
                RecipeId = recipeId,
                WorkspaceTagId = tag.WorkspaceTagId,
            });
        }

        return recipe;
    }
}
