using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

public enum RecipePdfPageSize
{
    A4 = 0,
    Letter = 1,
}

/// <summary>
/// A hero image the caller has already authorized and read. The renderer never fetches anything, so there is
/// no URL here to be tricked into reaching an arbitrary host.
/// </summary>
/// <param name="Bytes">PNG or JPEG content, checked by file signature and within the size and dimension limits.</param>
/// <param name="AltText">
/// The description the creator or the asset record supplies. Never written from the recipe: with none, the
/// image is marked decorative rather than described.
/// </param>
public sealed record RecipePdfImage(byte[] Bytes, string? AltText);

/// <summary>Everything <see cref="RecipePdfRenderer"/> may read. The renderer does no I/O.</summary>
public sealed record RecipePdfInput
{
    public required RecipeSnapshotDocument Snapshot { get; init; }

    public required int VersionNumber { get; init; }

    /// <summary>Stamped into the document metadata. Supplied so the renderer reads no clock.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    public RecipeVersionReadiness Readiness { get; init; }

    public RecipeExportTemplate Template { get; init; }

    public RecipePdfPageSize PageSize { get; init; }

    /// <summary>The document's natural language tag (BCP 47), which assistive technology reads aloud with.</summary>
    public string Language { get; init; } = "en-US";

    public RecipeUnitPresentation UnitPresentation { get; init; }

    public IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> UnitsById { get; init; } =
        new Dictionary<Guid, MeasurementUnitServiceModel>();

    public IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> TargetUnits { get; init; } =
        new Dictionary<MeasurementDimension, MeasurementUnitServiceModel>();

    public RecipeExportEditorial? Editorial { get; init; }

    public RecipePdfImage? Image { get; init; }
}

public sealed record RecipePdfIssue(string Code, string Message);

/// <summary>
/// The outcome of a render. <see cref="Pdf"/> is present only when nothing blocks it.
/// </summary>
/// <param name="Pdf">The PDF bytes, or <c>null</c> when <see cref="MissingRequired"/> is not empty.</param>
/// <param name="MissingRequired">Facts the document cannot be produced without, and conditions that block it.</param>
/// <param name="Warnings">Things left out or left as written that do not block.</param>
public sealed record RecipePdfReport(
    byte[]? Pdf,
    IReadOnlyList<RecipePdfIssue> MissingRequired,
    IReadOnlyList<RecipePdfIssue> Warnings)
{
    public bool IsComplete => Pdf is not null && MissingRequired.Count == 0;
}

/// <summary>Stable codes for <see cref="RecipePdfIssue"/>.</summary>
public static class RecipePdfCodes
{
    public const string RecipeNotApproved = "recipe_pdf_recipe_not_approved";
    public const string NameMissing = "recipe_pdf_name_missing";
    public const string IngredientsMissing = "recipe_pdf_ingredients_missing";
    public const string InstructionsMissing = "recipe_pdf_instructions_missing";
    public const string UnsupportedCharacters = "recipe_pdf_unsupported_characters";
    public const string UnitNotConverted = "recipe_pdf_unit_not_converted";
    public const string EditorialNotCurrent = "recipe_pdf_editorial_not_current";
    public const string SubstitutionUnmatched = "recipe_pdf_substitution_unmatched";
    public const string ImageInvalid = "recipe_pdf_image_invalid";
    public const string ImageAltMissing = "recipe_pdf_image_alt_missing";
}
