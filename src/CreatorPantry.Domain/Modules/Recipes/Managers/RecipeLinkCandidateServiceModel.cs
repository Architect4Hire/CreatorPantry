namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One of the workspace's own recipes that another piece of work may offer as a link target: its id and title,
/// nothing else. A recipe's content is not part of being linkable, so none of it crosses the module boundary.
/// </summary>
public sealed record RecipeLinkCandidateServiceModel(Guid Id, string Title);
