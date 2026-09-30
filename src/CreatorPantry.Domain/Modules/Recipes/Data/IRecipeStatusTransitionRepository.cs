using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// EF Core access for <see cref="RecipeStatusTransition"/>. Persistence only.
/// </summary>
/// <remarks>
/// One method, and deliberately only one. Writing a transition is the whole of what this module needs today:
/// reading a recipe's editorial history is 10.7c's route and will arrive with the projection and the paging
/// that route's contract calls for, rather than as a general-purpose list nothing yet consumes.
/// </remarks>
public interface IRecipeStatusTransitionRepository
{
    /// <summary>
    /// Stages one transition for insertion.
    /// </summary>
    /// <remarks>
    /// Synchronous and non-saving, for the reason <see cref="IRecipeVersionRepository.Add"/> is: the DataLayer
    /// decides when the unit commits, and a transition must commit in the same batch as the status change it
    /// records — a row saying a recipe was approved, beside a recipe that was not, is worse than no row.
    /// Nothing here updates or deletes, because nothing may: these rows are <see cref="IImmutableRecord"/>,
    /// and a method offering it would only be a way to discover that at runtime.
    /// </remarks>
    void Add(RecipeStatusTransition transition);
}

internal sealed class RecipeStatusTransitionRepository(CreatorPantryDbContext context)
    : IRecipeStatusTransitionRepository
{
    public void Add(RecipeStatusTransition transition) => context.RecipeStatusTransitions.Add(transition);
}
