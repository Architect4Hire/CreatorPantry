namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The units an export converts to for each presentation, by the measurement catalogue's stable codes. Fixed,
/// so two exports of the same version in the same presentation always agree.
/// </summary>
public static class RecipeExportTargetUnits
{
    public static IReadOnlyList<string> CodesFor(RecipeUnitPresentation presentation) => presentation switch
    {
        RecipeUnitPresentation.Metric => ["g", "ml"],
        RecipeUnitPresentation.UsCustomary => ["oz", "cup-us"],
        _ => [],
    };
}
