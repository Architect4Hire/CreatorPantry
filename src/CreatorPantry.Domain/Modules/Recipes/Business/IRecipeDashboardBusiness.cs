using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Business;

/// <summary>The recipe module's part of the workspace dashboard (13.11). Read-only; nothing is derived here that the data layer did not count.</summary>
public interface IRecipeDashboardBusiness
{
    Task<RecipeWorkSummaryServiceModel> GetWorkAsync(CancellationToken cancellationToken);

    Task<TestFollowUpSummaryServiceModel> GetTestFollowUpsAsync(CancellationToken cancellationToken);
}

internal sealed class RecipeDashboardBusiness(IRecipeDashboardDataLayer dataLayer) : IRecipeDashboardBusiness
{
    public Task<RecipeWorkSummaryServiceModel> GetWorkAsync(CancellationToken cancellationToken) =>
        dataLayer.GetWorkAsync(cancellationToken);

    public Task<TestFollowUpSummaryServiceModel> GetTestFollowUpsAsync(CancellationToken cancellationToken) =>
        dataLayer.GetTestFollowUpsAsync(cancellationToken);
}
