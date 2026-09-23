using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Translates a loaded recipe aggregate into the shape a client reads. Pure and total: no persistence, no
/// clock, no workspace, no authorization.
/// </summary>
/// <remarks>
/// <para>
/// Takes a <see cref="TaggedRecipe"/> rather than a <see cref="Recipe"/> for the same reason
/// <see cref="RecipeSnapshotMapper.Capture"/> takes a <see cref="CompleteRecipe"/>: it reads the child
/// collections directly, so a recipe loaded without its <c>Include</c>s would be published as an empty one.
/// Here that produces a recipe the creator sees as having lost its ingredients rather than an unrecoverable
/// archive, which is less severe and no less wrong.
/// </para>
/// <para>
/// Ordering is not applied here. The repository returns each collection sorted by its <c>SortOrder</c> index,
/// and re-sorting in the mapper would quietly excuse a query that stopped doing so — a bug that only shows up
/// in a caller which does not go through this mapper.
/// </para>
/// </remarks>
public static class RecipeDetailMapper
{
    public static RecipeDetailServiceModel ToDetail(TaggedRecipe loaded)
    {
        var recipe = loaded.Recipe.Recipe;

        return new RecipeDetailServiceModel
        {
            Id = recipe.Id,
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
            Status = recipe.Status,
            CreatedAt = recipe.CreatedAt,
            UpdatedAt = recipe.UpdatedAt,
            ConcurrencyToken = RecipeConcurrencyToken.From(recipe.RowVersion),
            CurrentVersion = ToSummary(loaded.Recipe.CurrentVersion),
            IngredientGroups = [.. recipe.IngredientGroups.Select(ToGroup)],
            InstructionGroups = [.. recipe.InstructionGroups.Select(ToGroup)],
            Equipment = [.. recipe.Equipment.Select(ToEquipment)],
            AssetLinks = [.. recipe.AssetLinks.Select(ToAssetLink)],
            Tags = ToTags(loaded),
        };
    }

    private static RecipeVersionSummaryServiceModel? ToSummary(RecipeVersion? version) =>
        version is null
            ? null
            : new RecipeVersionSummaryServiceModel
            {
                Id = version.Id,
                VersionNumber = version.VersionNumber,
                Source = version.Source,
                Readiness = version.Readiness,
                Reason = version.Reason,
                CreatedAt = version.CreatedAt,
            };

    /// <summary>
    /// The recipe's tags, named from the vocabulary rows loaded beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven from the links and not from <see cref="TaggedRecipe.Tags"/>, so a vocabulary row that arrived
    /// without a matching link cannot add a tag the recipe does not carry. A link with no matching row is
    /// dropped: the only way to reach that state is the race
    /// <see cref="TaggedRecipe"/> documents, and a nameless chip is worse than one fewer.
    /// </para>
    /// <para>
    /// Ordered by normalized name, and this is the one place ordering is decided rather than inherited from a
    /// query. Tags are a set — the repository says so, and stores no <c>SortOrder</c> — but a JSON array is
    /// observably ordered whatever the domain thinks, so leaving it to whatever order two reads happened to
    /// produce would make a creator's tag chips reshuffle between page loads. Pinning it here is also cheaper
    /// now than later: api-contract.md counts changing the ordering of a shipped collection as breaking.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RecipeTagServiceModel> ToTags(TaggedRecipe loaded)
    {
        var tags = loaded.Tags.ToDictionary(tag => tag.Id);

        return
        [
            .. loaded.Recipe.Recipe.Tags
                .Where(link => tags.ContainsKey(link.WorkspaceTagId))
                .Select(link => tags[link.WorkspaceTagId])
                .OrderBy(tag => tag.NormalizedName, StringComparer.Ordinal)
                .Select(tag => new RecipeTagServiceModel
                {
                    WorkspaceTagId = tag.Id,
                    Name = tag.Name,
                })
        ];
    }

    private static RecipeIngredientGroupServiceModel ToGroup(RecipeIngredientGroup group) => new()
    {
        Id = group.Id,
        Title = group.Title,
        SortOrder = group.SortOrder,
        Ingredients = [.. group.Ingredients.Select(ToIngredient)],
    };

    private static RecipeIngredientServiceModel ToIngredient(RecipeIngredient ingredient) => new()
    {
        Id = ingredient.Id,
        SortOrder = ingredient.SortOrder,
        DisplayText = ingredient.DisplayText,
        IngredientNameText = ingredient.IngredientNameText,
        Quantity = ingredient.Quantity,
        QuantityUpper = ingredient.QuantityUpper,
        MeasurementUnitId = ingredient.MeasurementUnitId,
        IngredientId = ingredient.IngredientId,
        MatchStatus = ingredient.MatchStatus,
        PreparationNote = ingredient.PreparationNote,
        IsOptional = ingredient.IsOptional,
        ScalingBehavior = ingredient.ScalingBehavior,
    };

    private static RecipeInstructionGroupServiceModel ToGroup(RecipeInstructionGroup group) => new()
    {
        Id = group.Id,
        Title = group.Title,
        SortOrder = group.SortOrder,
        Steps = [.. group.Steps.Select(ToStep)],
    };

    private static RecipeInstructionStepServiceModel ToStep(RecipeInstructionStep step) => new()
    {
        Id = step.Id,
        SortOrder = step.SortOrder,
        Text = step.Text,
        TechniqueId = step.TechniqueId,
        DurationMinutes = step.DurationMinutes,
        TemperatureValue = step.TemperatureValue,
        TemperatureUnitId = step.TemperatureUnitId,
        Note = step.Note,
    };

    private static RecipeEquipmentServiceModel ToEquipment(RecipeEquipment equipment) => new()
    {
        Id = equipment.Id,
        SortOrder = equipment.SortOrder,
        DisplayText = equipment.DisplayText,
        EquipmentTypeId = equipment.EquipmentTypeId,
        IsOptional = equipment.IsOptional,
        Note = equipment.Note,
    };

    private static RecipeAssetLinkServiceModel ToAssetLink(RecipeAssetLink link) => new()
    {
        Id = link.Id,
        SortOrder = link.SortOrder,
        MediaAssetId = link.MediaAssetId,
        Role = link.Role,
        Caption = link.Caption,
    };
}
