using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Facade;

/// <summary>
/// The application boundary for the published plan catalogue. Global: no workspace and no account is involved,
/// which is why the route over it may be anonymous.
/// </summary>
public interface IPlanCatalogueFacade
{
    /// <summary>The catalogue as published, or an empty one while it is not published.</summary>
    PlanCatalogueServiceModel Published();

    /// <summary>
    /// What a month of pictures and writing comes to on the published catalogue, and the smallest paid plan that
    /// carries it (LP.7, AP.7.2). Anonymous like the catalogue: two counts and a quality, and nothing about anyone.
    /// </summary>
    OperationResult<PlanEstimateServiceModel> Estimate(int picturesAMonth, int writingAMonth, string? qualityClass);
}

internal sealed class PlanCatalogueFacade(IPlanCatalogueBusiness business) : IPlanCatalogueFacade
{
    public PlanCatalogueServiceModel Published() => business.Published();

    public OperationResult<PlanEstimateServiceModel> Estimate(int picturesAMonth, int writingAMonth, string? qualityClass)
    {
        // Bounded here, at the edge of the module, so Business multiplies only what a month could hold.
        if (picturesAMonth is < 0 or > BillingPolicy.EstimateMaxPerMonth
            || writingAMonth is < 0 or > BillingPolicy.EstimateMaxPerMonth)
        {
            return OperationResult<PlanEstimateServiceModel>.Failure(OperationError.Validation(
                PlanEstimateErrors.Invalid,
                "That estimate could not be worked out.",
                [
                    ("pictures", $"Pictures a month are between 0 and {BillingPolicy.EstimateMaxPerMonth:N0}."),
                    ("writing", $"Writing a month is between 0 and {BillingPolicy.EstimateMaxPerMonth:N0}."),
                ]));
        }

        return business.Estimate(picturesAMonth, writingAMonth, qualityClass) is { } estimate
            ? OperationResult<PlanEstimateServiceModel>.Success(estimate)
            : OperationResult<PlanEstimateServiceModel>.Failure(new OperationError(
                PlanEstimateErrors.NotFound,
                "No plans are published to estimate against.",
                new Dictionary<string, string[]>()));
    }
}
