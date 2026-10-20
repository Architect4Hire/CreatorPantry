namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

/// <summary>A kind of high-risk method or claim the food-safety checks look for (14.5, B-37).</summary>
/// <remarks>
/// <see cref="Drying"/> and <see cref="OilInfusion"/> are here because the technique vocabulary already flags both
/// as needing a caution; leaving them out would ignore that flag. <see cref="OtherFlaggedMethod"/> is any technique
/// flagged later that this list does not name yet, so a new flag is reported rather than silently dropped.
/// </remarks>
public enum FoodSafetyCategory
{
    Canning = 1,
    Fermentation = 2,
    CuringOrSmoking = 3,
    SousVide = 4,
    RawOrUndercookedProtein = 5,
    InternalTemperature = 6,
    PregnancyOrMedicalDiet = 7,
    UnsupportedSafetyClaim = 8,
    Drying = 9,
    OilInfusion = 10,
    OtherFlaggedMethod = 11,
}

/// <summary>How firmly the recipe's own record shows the method or claim.</summary>
public enum FoodSafetyConfidence
{
    /// <summary>Wording that could describe it, or could be something ordinary. The creator decides which.</summary>
    Possible = 1,

    /// <summary>A flagged technique, or wording that names the method or makes the claim.</summary>
    Detected = 2,
}

/// <summary>What a trigger was read from.</summary>
public enum FoodSafetyTriggerKind
{
    /// <summary>A technique the creator chose, by its vocabulary code.</summary>
    Technique = 1,

    /// <summary>A span of the creator's own text.</summary>
    Text = 2,

    /// <summary>An ingredient line whose platform ingredient is in a category the check reads, such as seafood.</summary>
    IngredientCategory = 3,
}

/// <summary>Where in the recipe version a trigger was found.</summary>
public enum FoodSafetyField
{
    Title = 1,
    Description = 2,
    Headnote = 3,
    Notes = 4,
    StorageNotes = 5,
    PrimaryTechnique = 6,
    IngredientLine = 7,
    IngredientPreparation = 8,
    Step = 9,
    StepNote = 10,
    StepTechnique = 11,
    Equipment = 12,
    EquipmentNote = 13,
}

/// <summary>Whether a warning cites a reference, and if not, why verification is required instead.</summary>
public enum FoodSafetyEvidenceStatus
{
    /// <summary>An approved reference is cited.</summary>
    Cited = 1,

    /// <summary>Nothing is approved as a food-safety reference (B-33). The answer on every host today.</summary>
    NoApprovedSource = 2,

    /// <summary>An approved reference exists but this host has no provider for it.</summary>
    NotConfigured = 3,

    /// <summary>An approved reference exists but could not be read.</summary>
    Unavailable = 4,

    /// <summary>The approved reference was read and has nothing on this category.</summary>
    NoReference = 5,
}

public enum FoodSafetyTemperatureScale
{
    Fahrenheit = 1,
    Celsius = 2,
}

/// <param name="ItemId">The ingredient line, step or equipment row, or null for a field of the recipe itself.</param>
/// <param name="Matched">The span of the creator's text that matched, or the technique's code.</param>
public sealed record FoodSafetyTriggerServiceModel(
    FoodSafetyField Field, Guid? ItemId, FoodSafetyTriggerKind Kind, string Matched);

/// <summary>One approved reference, cited with everything needed to find it again and honour its licence.</summary>
public sealed record FoodSafetyCitationServiceModel(
    string SourceCode,
    string SourceName,
    string SourceVersion,
    string Citation,
    string? LicenseCode,
    string? Url,
    DateTimeOffset RetrievedAt);

/// <param name="Citation">Present exactly when <see cref="Status"/> is <see cref="FoodSafetyEvidenceStatus.Cited"/>.</param>
/// <param name="Statement">What the evidence is, in words, for the creator.</param>
public sealed record FoodSafetyEvidenceServiceModel(
    FoodSafetyEvidenceStatus Status, FoodSafetyCitationServiceModel? Citation, string Statement)
{
    public bool VerificationRequired => Status is not FoodSafetyEvidenceStatus.Cited;
}

/// <summary>An internal temperature the recipe states, recorded as written. Never compared without a reference.</summary>
public sealed record FoodSafetyStatedTemperatureServiceModel(
    decimal Value, FoodSafetyTemperatureScale Scale, FoodSafetyField Field, Guid? ItemId);

/// <summary>One category's warning, with every trigger that raised it.</summary>
/// <param name="StatedTemperatures">Only on <see cref="FoodSafetyCategory.InternalTemperature"/>; empty elsewhere.</param>
public sealed record FoodSafetyWarningServiceModel(
    FoodSafetyCategory Category,
    FoodSafetyConfidence Confidence,
    string Message,
    IReadOnlyList<FoodSafetyTriggerServiceModel> Triggers,
    FoodSafetyEvidenceServiceModel Evidence,
    IReadOnlyList<FoodSafetyStatedTemperatureServiceModel> StatedTemperatures);

/// <summary>The food-safety warnings for one recipe version (14.5). Read-only, and never a verdict.</summary>
/// <param name="UnrecognisedTechniques">
/// Techniques the version names that are not in the active vocabulary, so their flags could not be read. Their
/// steps' text was still read; a non-zero count means an empty <paramref name="Warnings"/> was read from less than
/// the whole record.
/// </param>
/// <param name="Disclaimer">On every result, whatever it found — an empty one most of all.</param>
public sealed record FoodSafetyWarningsServiceModel(
    Guid RecipeId,
    Guid RecipeVersionId,
    int VersionNumber,
    string MethodVersion,
    IReadOnlyList<FoodSafetyWarningServiceModel> Warnings,
    int UnrecognisedTechniques,
    string Disclaimer);

/// <summary>
/// What a warning is checked against. Today every host passes <see cref="Uncited"/> with
/// <see cref="FoodSafetyEvidenceStatus.NoApprovedSource"/>, because nothing is approved (B-33).
/// </summary>
/// <param name="WhenNotCited">The reason given for any category without a citation. Never <see cref="FoodSafetyEvidenceStatus.Cited"/>.</param>
/// <param name="Citations">An approved reference per category, when one was read.</param>
public sealed record FoodSafetyEvidenceInput(
    FoodSafetyEvidenceStatus WhenNotCited,
    IReadOnlyDictionary<FoodSafetyCategory, FoodSafetyCitationServiceModel> Citations)
{
    public static FoodSafetyEvidenceInput Uncited(FoodSafetyEvidenceStatus reason) =>
        new(reason, new Dictionary<FoodSafetyCategory, FoodSafetyCitationServiceModel>());
}

public sealed record FoodSafetyLineInput(Guid Id, string DisplayText, string? PreparationNote, Guid? IngredientId);

public sealed record FoodSafetyStepInput(Guid Id, string Text, string? Note, Guid? TechniqueId);

public sealed record FoodSafetyEquipmentInput(Guid Id, string DisplayText, string? Note);

/// <summary>The parts of one immutable recipe version the checks read, in the order the creator sees them.</summary>
public sealed record FoodSafetyRecipeInput(
    int VersionNumber,
    string Title,
    string? Description,
    string? Headnote,
    string? Notes,
    string? StorageNotes,
    Guid? PrimaryTechniqueId,
    IReadOnlyList<FoodSafetyLineInput> Lines,
    IReadOnlyList<FoodSafetyStepInput> Steps,
    IReadOnlyList<FoodSafetyEquipmentInput> Equipment);

/// <summary>The fixed words of every food-safety result (14.5, B-37). Change one, change the method version.</summary>
/// <remarks>
/// Every sentence says what the recipe's record shows and what needs checking. None says what is safe, what the
/// hazard is or what a reader should do: each of those would be a food-safety claim of its own, and this module has
/// no approved reference to make one from. A guard test holds that for every sentence here.
/// </remarks>
public static class FoodSafetyWarningPolicy
{
    public const string MethodVersion = "food-safety-warnings/1";

    /// <summary>The longest span of creator text a trigger quotes.</summary>
    public const int MaxMatchedLength = 120;

    public const string Disclaimer =
        "These checks look for a fixed set of high-risk methods and claims in the recipe's own text and choices. "
        + "Finding none is not a statement that the recipe is safe, and a warning is not a ruling that it is unsafe. "
        + "Check methods, temperatures and claims against current, tested guidance before publishing.";

    public static string Message(FoodSafetyCategory category, FoodSafetyConfidence confidence, string? techniqueName = null) =>
        (category, confidence) switch
        {
            (FoodSafetyCategory.Canning, FoodSafetyConfidence.Detected) =>
                "This recipe describes home canning. Jar processing, times and acidity need checking against tested canning guidance.",
            (FoodSafetyCategory.Canning, _) =>
                "This recipe may describe home canning: jars are sealed for the shelf or go into a water bath. If it does, check processing, times and acidity against tested canning guidance.",

            (FoodSafetyCategory.Fermentation, FoodSafetyConfidence.Detected) =>
                "This recipe describes fermenting food. Salt, temperature, time and signs of spoilage need checking against tested guidance.",
            (FoodSafetyCategory.Fermentation, _) =>
                "This recipe may describe fermentation: food is left out for days. If it does, check the method against tested guidance.",

            (FoodSafetyCategory.CuringOrSmoking, FoodSafetyConfidence.Detected) =>
                "This recipe describes curing or smoking. Curing agents, amounts, temperatures and times need checking against tested guidance.",
            (FoodSafetyCategory.CuringOrSmoking, _) =>
                "This recipe mentions pink salt. If that means a curing salt rather than a pink table salt, amounts and method need checking against tested guidance.",

            (FoodSafetyCategory.SousVide, FoodSafetyConfidence.Detected) =>
                "This recipe uses sous vide. Water temperature and holding time need checking against tested guidance.",
            (FoodSafetyCategory.SousVide, _) =>
                "This recipe may describe sous vide cooking: a water bath held at a low temperature for hours. If it does, check temperature and time against tested guidance.",

            (FoodSafetyCategory.RawOrUndercookedProtein, FoodSafetyConfidence.Detected) =>
                "This recipe appears to serve meat, poultry, seafood or eggs raw or lightly cooked. Check the method and any advice to readers against tested guidance.",
            (FoodSafetyCategory.RawOrUndercookedProtein, _) =>
                "This recipe may serve meat, poultry, seafood or eggs raw or lightly cooked, for example rare, tartare or with a runny yolk. If it does, check the method and any advice to readers against tested guidance.",

            (FoodSafetyCategory.InternalTemperature, FoodSafetyConfidence.Detected) =>
                "This recipe states an internal temperature. It has not been compared with a reference figure for this food.",
            (FoodSafetyCategory.InternalTemperature, _) =>
                "This recipe uses a thermometer as its doneness test but gives no figure beside it. Check the doneness test against tested guidance.",

            (FoodSafetyCategory.PregnancyOrMedicalDiet, FoodSafetyConfidence.Detected) =>
                "This recipe says it suits a pregnancy, a medical condition, babies or young children. That is a medical claim, and it needs a vetted reference before publishing.",
            (FoodSafetyCategory.PregnancyOrMedicalDiet, _) =>
                "This recipe mentions pregnancy or a medical condition. Check that nothing reads as a promise that it suits anyone.",

            (FoodSafetyCategory.UnsupportedSafetyClaim, FoodSafetyConfidence.Detected) =>
                "This recipe makes a claim about food safety or keeping quality. A claim like this needs a vetted reference before publishing.",
            (FoodSafetyCategory.UnsupportedSafetyClaim, _) =>
                "This recipe gives a keeping time without saying how the food is stored. Check that it does not read as a claim about safety.",

            (FoodSafetyCategory.Drying, _) =>
                "This recipe describes drying or dehydrating food. Temperatures and times need checking against tested guidance.",

            (FoodSafetyCategory.OilInfusion, FoodSafetyConfidence.Detected) =>
                "This recipe infuses oil with fresh ingredients such as garlic or herbs. Storage and keeping time need checking against tested guidance.",
            (FoodSafetyCategory.OilInfusion, _) =>
                "This recipe mentions an infused oil. If it is made rather than bought, storage and keeping time need checking against tested guidance.",

            _ =>
                $"This recipe uses {techniqueName ?? "a method"}, which the technique catalogue marks as needing a food-safety caution. Check the method against tested guidance.",
        };

    public static string Statement(FoodSafetyEvidenceStatus status, FoodSafetyCitationServiceModel? citation = null) => status switch
    {
        FoodSafetyEvidenceStatus.Cited when citation is not null =>
            $"Check this against {citation.SourceName}: {citation.Citation} Version {citation.SourceVersion}, retrieved {citation.RetrievedAt.UtcDateTime:yyyy-MM-dd}.",
        FoodSafetyEvidenceStatus.NotConfigured =>
            "Verification required: the approved food-safety reference is not available on this installation. Check this against current, tested guidance before publishing.",
        FoodSafetyEvidenceStatus.Unavailable =>
            "Verification required: the approved food-safety reference could not be read. Check this against current, tested guidance before publishing.",
        FoodSafetyEvidenceStatus.NoReference =>
            "Verification required: the approved food-safety reference has nothing on this. Check this against current, tested guidance before publishing.",
        _ =>
            "Verification required: CreatorPantry has no approved food-safety reference to check this against. Check it against current, tested guidance before publishing.",
    };
}
