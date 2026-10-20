namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

/// <summary>Limits for nutrition mappings, shared by the EF configuration and the validators.</summary>
/// <remarks>
/// The reference widths match what the Ingredients module's evidence contract can return. They are restated
/// rather than referenced because a policy type does not cross a module boundary; a test holds them equal.
/// </remarks>
public static class NutritionMappingPolicy
{
    public const int SourceCodeMaxLength = 64;

    public const int ExternalRecordIdMaxLength = 32;

    public const int DescriptionMaxLength = 512;

    /// <summary>A data set and a date, e.g. <c>sr-legacy/2019-04-01</c>.</summary>
    public const int SourceVersionMaxLength = 64;

    public const int PortionLabelMaxLength = 128;

    /// <summary>SQL precision and scale for a portion's amount and weight.</summary>
    public const int PortionPrecision = 12;

    public const int PortionScale = 4;

    /// <summary>A portion is a household measure: a few cups, a few pieces.</summary>
    public const decimal PortionAmountMax = 1000m;

    /// <summary>Ten kilograms for one portion is already absurd; past it is a typing error.</summary>
    public const decimal PortionGramsMax = 10_000m;

    /// <summary>The most ingredients one list read names — far more than any recipe has.</summary>
    public const int MaxListIngredients = 500;
}

/// <summary>The stable codes the nutrition mapping seam refuses with. Append-only once a route returns them.</summary>
/// <remarks>
/// Four mapping codes were corrected in 14.6b, before any route had returned them, because their underscored
/// endings would have answered 400 through <c>ProblemResults.StatusFor</c>: <c>stale</c> → <c>conflict</c>,
/// <c>state_conflict</c> → <c>state.conflict</c>, <c>source_unavailable</c> → <c>source.unavailable</c>,
/// <c>not_saved</c> → <c>save.unavailable</c> (B-40).
/// </remarks>
public static class NutritionErrorCodes
{
    /// <summary>The request could not be read as described. Carries field errors.</summary>
    public const string MappingInvalid = "nutrition.mapping.invalid";

    /// <summary>The caller's role may read this workspace's mappings but not change them this way.</summary>
    public const string MappingForbidden = "nutrition.mapping.forbidden";

    /// <summary>This workspace has no mapping for that ingredient.</summary>
    public const string MappingNotFound = "nutrition.mapping.not_found";

    /// <summary>The ingredient is not one the platform catalogue offers, or is retired.</summary>
    public const string IngredientUnprocessable = "nutrition.mapping.ingredient.unprocessable";

    /// <summary>The mapping changed since the caller read it, as its concurrency token shows. 409; nothing was written.</summary>
    public const string MappingStale = "nutrition.mapping.conflict";

    /// <summary>The mapping is not in a state that allows this; the message says which state it is in.</summary>
    public const string MappingConflict = "nutrition.mapping.state.conflict";

    /// <summary>
    /// The reference named is not approved evidence: no such record, or not from an approved data set. One code
    /// for both, as for any reference the caller cannot use.
    /// </summary>
    public const string ReferenceUnprocessable = "nutrition.mapping.reference.unprocessable";

    /// <summary>The portion's unit is not a usable volume or count unit.</summary>
    public const string PortionUnprocessable = "nutrition.mapping.portion.unprocessable";

    /// <summary>
    /// The evidence source could not be asked just then — not configured, throttled or down. 503; nothing was
    /// written, and nothing is concluded from the silence.
    /// </summary>
    public const string SourceUnavailable = "nutrition.mapping.source.unavailable";

    /// <summary>The store refused the write and nothing was written. 503: safe to send again.</summary>
    public const string MappingNotSaved = "nutrition.mapping.save.unavailable";

    /// <summary>The estimate request could not be read as described. Carries field errors.</summary>
    public const string EstimateInvalid = "nutrition.estimate.invalid";

    /// <summary>This workspace has no such recipe, or that recipe no such version.</summary>
    /// <remarks>
    /// Was <c>nutrition.estimate.recipe_not_found</c>, which <c>ProblemResults.StatusFor</c> would have answered 400
    /// for want of a <c>.not_found</c> suffix. Corrected in 14.6, before any route had returned it (B-38).
    /// </remarks>
    public const string EstimateRecipeNotFound = "nutrition.estimate.recipe.not_found";

    /// <summary>A total is too large to state. Refused rather than overflowed.</summary>
    /// <remarks>
    /// Was <c>nutrition.estimate.out_of_range</c>, which would have answered 400 as if the request were at fault. The
    /// request is well formed; the recipe's quantities are what cannot be stated (14.6a, B-39).
    /// </remarks>
    public const string EstimateQuantitiesUnprocessable = "nutrition.estimate.quantities.unprocessable";

    /// <summary>
    /// The nutrition source could not be asked, so no estimate was made rather than a smaller one. 503, and nothing was
    /// written. Down or throttled clears by waiting; not available on this installation does not, until it is
    /// configured — the message says which.
    /// </summary>
    /// <remarks>
    /// The estimate's own, separate from <see cref="SourceUnavailable"/>, which the mapping commands answer with.
    /// </remarks>
    public const string EstimateSourceUnavailable = "nutrition.estimate.source.unavailable";

    /// <summary>The dietary and allergen analysis request could not be read as described. Carries field errors.</summary>
    public const string AnalysisInvalid = "nutrition.analysis.invalid";

    /// <summary>An allergen or dietary profile asked about is not one the catalogue offers.</summary>
    public const string AnalysisSubjectUnprocessable = "nutrition.analysis.subject.unprocessable";
}

/// <summary>
/// Audit action codes for nutrition mappings. Append-only: renaming one orphans the history written under it.
/// </summary>
/// <remarks>
/// A creator's confirmation, rejection and remapping decide what an estimate rests on, so each is recorded.
/// Proposals and refreshes are the system's own reads and are not — except a refresh that withdraws or flags a
/// confirmed reference, which changes what an estimate may use.
/// </remarks>
public static class NutritionAuditActions
{
    public const string ResourceType = "NutritionMapping";

    public const string Confirmed = "nutrition.mapping.confirmed";

    public const string Rejected = "nutrition.mapping.rejected";

    public const string Remapped = "nutrition.mapping.remapped";

    public const string MarkedUnresolved = "nutrition.mapping.marked_unresolved";

    public const string FlaggedForReview = "nutrition.mapping.flagged_for_review";

    public const string ReferenceWithdrawn = "nutrition.mapping.reference_withdrawn";
}
