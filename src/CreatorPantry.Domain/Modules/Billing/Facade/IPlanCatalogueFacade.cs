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
}

internal sealed class PlanCatalogueFacade(IPlanCatalogueBusiness business) : IPlanCatalogueFacade
{
    public PlanCatalogueServiceModel Published() => business.Published();
}
