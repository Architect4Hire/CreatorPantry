using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The boundary another module crosses to learn the long-form shape rules and to have a plan checked against
/// them (LF.2.1).
/// </summary>
/// <remarks>
/// <para>
/// A facade because the rules are the content module's and a policy never crosses a module boundary: the AI
/// module is told the figures and is handed a verdict, and can change neither.
/// </para>
/// <para>
/// <strong>No workspace and no I/O.</strong> The rules are the same for every workspace and are code, so
/// there is nothing here to authorize and nothing to await.
/// </para>
/// </remarks>
public interface ILongFormPlanFacade
{
    /// <inheritdoc cref="ILongFormPlanBusiness.Describe"/>
    LongFormPlanRulesServiceModel Describe(LongFormKind kind);

    /// <inheritdoc cref="ILongFormPlanBusiness.Check"/>
    IReadOnlyList<LongFormPlanFindingServiceModel> Check(
        LongFormKind kind, IReadOnlyList<LongFormPlanSectionServiceModel> sections);

    /// <inheritdoc cref="ILongFormPlanBusiness.Measure" />
    LongFormBodyMeasureServiceModel Measure(string body);
}

/// <inheritdoc cref="ILongFormPlanFacade"/>
internal sealed class LongFormPlanFacade(ILongFormPlanBusiness business) : ILongFormPlanFacade
{
    public LongFormPlanRulesServiceModel Describe(LongFormKind kind) => business.Describe(kind);

    public IReadOnlyList<LongFormPlanFindingServiceModel> Check(
        LongFormKind kind, IReadOnlyList<LongFormPlanSectionServiceModel> sections) =>
        business.Check(kind, sections);

    public LongFormBodyMeasureServiceModel Measure(string body) => business.Measure(body);
}
