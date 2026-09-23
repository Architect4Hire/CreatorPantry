namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// A regional or cultural cooking tradition a recipe can be described against — <c>italian</c>,
/// <c>thai</c>, <c>tex-mex</c>. Global reference data with no <c>WorkspaceId</c> (tenancy.md).
/// </summary>
/// <remarks>
/// A flat list by design. Cuisines nest in every direction at once — Sichuan inside Chinese, Cajun beside
/// Creole inside Southern inside American — and a single parent column would force one of those readings
/// on everyone while answering no question the product asks today. A recipe that is both is described by
/// its workspace tags, which are the creator's to define.
/// </remarks>
public class Cuisine : ControlledVocabulary;
