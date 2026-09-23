using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// EF Core access for <see cref="RecipeVersion"/>, which is an aggregate root of its own rather than part of
/// the recipe — it outlives the edits that supersede it and is read independently. Persistence only.
/// </summary>
public interface IRecipeVersionRepository
{
    /// <summary>
    /// Stages a version and, when one is attached, its snapshot for insertion.
    /// </summary>
    /// <remarks>
    /// Synchronous and non-saving, for the same reason <see cref="IRecipeRepository.Add"/> is: the DataLayer
    /// decides when the unit commits, and a version must commit in the same batch as the content it
    /// describes. Nothing here updates or deletes, because nothing may — these rows are
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IImmutableRecord"/>, and a repository method that
    /// offered it would only be a way to find that out at runtime.
    /// </remarks>
    void Add(RecipeVersion version);
}

internal sealed class RecipeVersionRepository(CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext context)
    : IRecipeVersionRepository
{
    public void Add(RecipeVersion version) => context.RecipeVersions.Add(version);
}
