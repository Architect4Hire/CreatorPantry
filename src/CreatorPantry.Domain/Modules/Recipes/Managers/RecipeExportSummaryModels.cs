namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>Stable reason codes for a version that cannot be exported.</summary>
public static class RecipeExportSummaryReasons
{
    public const string NotApproved = "recipe_export_not_approved";
}

/// <summary>One kind of accepted copy and whether it would be included in an export of the current version.</summary>
/// <param name="RevisionNumber">The accepted revision, as the content history numbers it.</param>
/// <param name="IsCurrent">
/// False when the copy was accepted for an older version, or has been marked for review since. Such copy is
/// left out of an export and listed as a warning instead.
/// </param>
public sealed record RecipeExportAcceptedCopyServiceModel(int RevisionNumber, bool IsCurrent);

/// <summary>
/// What an export of a recipe's current version would be built from. Carries no content, no path and no
/// provider or storage address: the panel that shows it builds its download links from the route alone.
/// </summary>
/// <param name="VersionNumber">The recipe's current version, the one an export without a choice uses.</param>
/// <param name="Exportable">True when the version is approved or marked ready.</param>
/// <param name="NotExportableReason">A <see cref="RecipeExportSummaryReasons"/> code when not exportable, else null.</param>
/// <param name="Editorial">The accepted editorial revision, or null when none is accepted.</param>
/// <param name="Seo">The accepted SEO revision, or null when none is accepted.</param>
public sealed record RecipeExportSummaryServiceModel(
    int VersionNumber,
    bool Exportable,
    string? NotExportableReason,
    RecipeExportAcceptedCopyServiceModel? Editorial,
    RecipeExportAcceptedCopyServiceModel? Seo);

/// <summary>The current version a summary is about, before its accepted copy is looked up.</summary>
public sealed record RecipeExportSummarySource(Guid VersionId, int VersionNumber, bool Exportable);
