using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// What the dashboard asks the recipe module (13.11): recipes in progress and unresolved test issues, each a
/// few counts and a capped list.
/// </summary>
/// <remarks>
/// Read by every member: the workspace route policy and the resolved context are the authorization, and the
/// workspace query filter is what scopes every read. Not cached — the dashboard is workspace-private and
/// changes with every edit.
/// </remarks>
public interface IRecipeDashboardFacade
{
    /// <inheritdoc cref="IRecipeDashboardBusiness.GetWorkAsync"/>
    Task<RecipeWorkSummaryServiceModel> GetWorkAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="IRecipeDashboardBusiness.GetTestFollowUpsAsync"/>
    Task<TestFollowUpSummaryServiceModel> GetTestFollowUpsAsync(CancellationToken cancellationToken);
}

internal sealed class RecipeDashboardFacade(IRecipeDashboardBusiness business) : IRecipeDashboardFacade
{
    public Task<RecipeWorkSummaryServiceModel> GetWorkAsync(CancellationToken cancellationToken) =>
        business.GetWorkAsync(cancellationToken);

    public Task<TestFollowUpSummaryServiceModel> GetTestFollowUpsAsync(CancellationToken cancellationToken) =>
        business.GetTestFollowUpsAsync(cancellationToken);
}
