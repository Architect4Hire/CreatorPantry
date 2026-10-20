using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Facade;

/// <summary>
/// The high-risk method and claim warnings for one recipe version (14.5). Read-only, for any member; no route serves
/// it yet.
/// </summary>
/// <remarks>
/// Warnings are review signals, never a ruling. None blocks a recipe, and an empty list is not a statement that
/// anything is safe — the result's disclaimer says so on every answer. Anything that explains a warning later, a
/// model included, takes it as input and cannot remove or soften it (ai.md).
/// </remarks>
public interface IFoodSafetyWarningFacade
{
    Task<OperationResult<FoodSafetyWarningsServiceModel>> DetectAsync(
        Guid recipeId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>The same checks for the version the history lists under <paramref name="versionNumber"/>.</summary>
    /// <remarks>
    /// What the route uses (14.7, H-3). Every public recipe route names a version by number, and until this
    /// existed these checks had no route at all: a recipe the detector flagged for canning or curing could
    /// reach Approved with nothing said to the creator anywhere.
    /// </remarks>
    Task<OperationResult<FoodSafetyWarningsServiceModel>> DetectByNumberAsync(
        Guid recipeId, int versionNumber, CancellationToken cancellationToken);
}

internal sealed class FoodSafetyWarningFacade(IFoodSafetyWarningBusiness business) : IFoodSafetyWarningFacade
{
    // Nothing to validate: two route ids, and a version this workspace cannot read is not found.
    public Task<OperationResult<FoodSafetyWarningsServiceModel>> DetectAsync(
        Guid recipeId, Guid versionId, CancellationToken cancellationToken) =>
        business.DetectAsync(recipeId, versionId, cancellationToken);

    public Task<OperationResult<FoodSafetyWarningsServiceModel>> DetectByNumberAsync(
        Guid recipeId, int versionNumber, CancellationToken cancellationToken) =>
        business.DetectByNumberAsync(recipeId, versionNumber, cancellationToken);
}
