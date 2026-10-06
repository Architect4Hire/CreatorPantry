namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>What a repository read of a recipe's name found: its id and its title, and nothing else.</summary>
/// <remarks>
/// A <c>Record</c> rather than a ServiceModel because it does not cross a module boundary —
/// <see cref="RecipeLinkCandidateServiceModel"/> is what the facade returns, and the two are deliberately
/// separate so that widening the internal read never widens the published one.
/// </remarks>
public sealed record RecipeTitleRecord(Guid Id, string Title);
