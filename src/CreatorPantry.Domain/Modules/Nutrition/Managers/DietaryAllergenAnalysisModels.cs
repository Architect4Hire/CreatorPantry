using CreatorPantry.Domain.Modules.Ingredients.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

// ---- Findings ----
//
// There is no member meaning safe, free from, certified or suitable in any of these, and none may be added
// (recipes.md, ai.md). A guard test holds that.

/// <summary>What the approved records say about one allergen in one recipe line.</summary>
public enum AllergenLineFinding
{
    /// <summary>No reviewed record, or not a recognised ingredient. Never absent.</summary>
    Unknown = 1,

    /// <summary>At least one source states the allergen is present.</summary>
    Contains = 2,

    /// <summary>A source states it is possible — "may contain", shared equipment, a variable supply.</summary>
    Possible = 3,

    /// <summary>Every source that speaks published a composition without it. Not "free from".</summary>
    NotListed = 4,
}

/// <summary>What the approved records say about one recipe line against one dietary profile.</summary>
public enum DietaryLineFinding
{
    Unknown = 1,
    Incompatible = 2,

    /// <summary>It varies by brand, process or certification, as the source explains.</summary>
    DependsOnProduct = 3,
    Compatible = 4,
}

/// <summary>One allergen across the whole recipe: the most cautious of its lines.</summary>
public enum RecipeAllergenFinding
{
    Contains = 1,
    Possible = 2,
    Unknown = 3,

    /// <summary>
    /// No line's records list it, and every line has records. A statement about the records, never that the
    /// recipe is free of the allergen.
    /// </summary>
    NotListedInRecords = 4,
}

/// <summary>One dietary profile across the whole recipe: the most cautious of its lines.</summary>
public enum RecipeDietaryFinding
{
    DoesNotFit = 1,
    DependsOnProduct = 2,
    Unknown = 3,

    /// <summary>Every line's source describes it as compatible. Descriptive of composition, never medical.</summary>
    FitsAsRecorded = 4,
}

/// <summary>Why a line's finding is what it is when there is no evidence to point at.</summary>
public enum TraitFindingReason
{
    None = 0,

    /// <summary>The ingredient has no approved record about this subject.</summary>
    NoRecord = 1,

    /// <summary>The line is not matched to a platform ingredient. Its own text is never read for allergens.</summary>
    NotARecognisedIngredient = 2,
}

/// <summary>
/// How complete the records behind one allergen's or profile's finding are (14.6). A statement about the records,
/// not about how likely the finding is: a <see cref="RecipeAllergenFinding.Contains"/> on partial records is still
/// a finding that the recipe contains it.
/// </summary>
public enum AnalysisConfidence
{
    /// <summary>No line has a record that counts. The finding is <c>Unknown</c>.</summary>
    None = 1,

    /// <summary>Some lines have records that count and some have none; the finding rests on the ones that do.</summary>
    Partial = 2,

    /// <summary>Approved sources disagree about at least one line. The more cautious word was taken; both are cited.</summary>
    Disputed = 3,

    /// <summary>Every line has a record that counts, and no sources disagree.</summary>
    Recorded = 4,
}

/// <summary>How many lines were read, and how many of them have at least one record that counts.</summary>
public sealed record AnalysisCoverageServiceModel(int LinesRead, int LinesWithCountedRecords);

/// <summary>
/// One source an analysis cited, with the latest of its records it read (14.6). Trait records carry no version of
/// their own; a source and the date its statement took effect are what a reader checks a record against.
/// </summary>
/// <param name="LatestEffectiveFrom">The newest effective date among this source's records the analysis read.</param>
/// <param name="IsActive">False for a retired source: cited, never counted as reassurance.</param>
public sealed record AnalysisSourceServiceModel(
    string Code,
    string Name,
    ReferenceSourceKind Kind,
    string Citation,
    string? LicenseCode,
    string? LicenseUrl,
    bool IsActive,
    DateOnly LatestEffectiveFrom);

// ---- Evidence ----

/// <summary>One source's approved word about an allergen in an ingredient, as the analysis read it.</summary>
/// <param name="Counted">
/// False for a reassuring word — "not listed" — from a source that may not give one: an unvetted kind, or a
/// retired source. Cited so a reader can see it, and never allowed to move a finding away from caution.
/// </param>
public sealed record AllergenTraitEvidenceServiceModel(
    AllergenPresence Presence,
    string SourceCode,
    string SourceName,
    ReferenceSourceKind SourceKind,
    string Citation,
    DateOnly EffectiveFrom,
    string EvidenceNote,
    bool Counted);

/// <summary>One source's approved word about an ingredient against a dietary profile.</summary>
/// <param name="Counted">False for "compatible" from a source that may not reassure; see <see cref="AllergenTraitEvidenceServiceModel.Counted"/>.</param>
public sealed record DietaryTraitEvidenceServiceModel(
    DietaryCompatibility Compatibility,
    string SourceCode,
    string SourceName,
    ReferenceSourceKind SourceKind,
    string Citation,
    DateOnly EffectiveFrom,
    string EvidenceNote,
    bool Counted);

/// <param name="Conflict">Approved sources disagree; the finding is the more cautious one and both are cited.</param>
public sealed record AllergenLineAnalysisServiceModel(
    Guid LineId,
    string DisplayText,
    bool IsOptional,
    Guid? IngredientId,
    AllergenLineFinding Finding,
    TraitFindingReason Reason,
    bool Conflict,
    IReadOnlyList<AllergenTraitEvidenceServiceModel> Evidence);

public sealed record DietaryLineAnalysisServiceModel(
    Guid LineId,
    string DisplayText,
    bool IsOptional,
    Guid? IngredientId,
    DietaryLineFinding Finding,
    TraitFindingReason Reason,
    bool Conflict,
    IReadOnlyList<DietaryTraitEvidenceServiceModel> Evidence);

/// <summary>One selected allergen across the recipe.</summary>
/// <param name="Statement">The finding in one fixed sentence. Generated from the finding, never from a model.</param>
/// <param name="PossibleSources">
/// Lines whose sources say the allergen is possible: the hidden sources and cross-contact the records know of.
/// </param>
public sealed record AllergenAnalysisServiceModel(
    Guid AllergenId,
    string AllergenName,
    RecipeAllergenFinding Finding,
    AnalysisConfidence Confidence,
    AnalysisCoverageServiceModel Coverage,
    string Statement,
    IReadOnlyList<Guid> ContainingLines,
    IReadOnlyList<Guid> PossibleSources,
    IReadOnlyList<Guid> UnknownLines,
    IReadOnlyList<Guid> ConflictingLines,
    IReadOnlyList<AllergenLineAnalysisServiceModel> Lines);

/// <summary>One selected dietary profile across the recipe.</summary>
/// <param name="DependsOnProductLines">Lines whose fit varies by product: the hidden sources the records know of.</param>
public sealed record DietaryAnalysisServiceModel(
    Guid DietaryProfileId,
    string DietaryProfileName,
    RecipeDietaryFinding Finding,
    AnalysisConfidence Confidence,
    AnalysisCoverageServiceModel Coverage,
    string Statement,
    IReadOnlyList<Guid> IncompatibleLines,
    IReadOnlyList<Guid> DependsOnProductLines,
    IReadOnlyList<Guid> UnknownLines,
    IReadOnlyList<Guid> ConflictingLines,
    IReadOnlyList<DietaryLineAnalysisServiceModel> Lines);

/// <summary>
/// A deterministic dietary and allergen analysis of one recipe version against selected profiles (14.4, ING-007).
/// A reading of approved ingredient records — never a certification, and never a statement about a person.
/// </summary>
/// <param name="EvaluatedOn">The date the records were read as of (UTC). A trait dated later was not yet in effect.</param>
/// <param name="CrossContactDisclaimer">On every analysis, whatever it found.</param>
/// <param name="Sources">Every source cited anywhere in the analysis, counted or not, by code.</param>
public sealed record DietaryAllergenAnalysisServiceModel(
    Guid RecipeId,
    Guid RecipeVersionId,
    int VersionNumber,
    string MethodVersion,
    DateOnly EvaluatedOn,
    string CrossContactDisclaimer,
    IReadOnlyList<AllergenAnalysisServiceModel> Allergens,
    IReadOnlyList<DietaryAnalysisServiceModel> Dietary,
    IReadOnlyList<AnalysisSourceServiceModel> Sources);

// ---- What is asked ----

/// <summary>The allergens and dietary profiles to analyse a recipe version against.</summary>
/// <remarks>
/// A JSON <c>null</c> for either list reads as an empty one, so an explicit null is the same request as an omitted
/// field rather than a crash in validation.
/// </remarks>
public sealed class DietaryAllergenSelectionViewModel
{
    private List<Guid> _allergenIds = [];
    private List<Guid> _dietaryProfileIds = [];

    public List<Guid> AllergenIds
    {
        get => _allergenIds;
        set => _allergenIds = value ?? [];
    }

    public List<Guid> DietaryProfileIds
    {
        get => _dietaryProfileIds;
        set => _dietaryProfileIds = value ?? [];
    }
}

internal sealed class DietaryAllergenSelectionViewModelValidator : AbstractValidator<DietaryAllergenSelectionViewModel>
{
    public DietaryAllergenSelectionViewModelValidator()
    {
        RuleFor(model => model)
            .Must(model => model.AllergenIds.Count + model.DietaryProfileIds.Count > 0)
            .WithName("allergenIds")
            .WithMessage("Choose at least one allergen or dietary profile.");

        RuleFor(model => model.AllergenIds)
            .Must(ids => ids.Count <= DietaryAllergenAnalysisPolicy.MaxSelected)
            .WithMessage($"Choose no more than {DietaryAllergenAnalysisPolicy.MaxSelected} allergens.")
            .Must(ids => ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count)
            .WithMessage("Name each allergen once, by its id.");

        RuleFor(model => model.DietaryProfileIds)
            .Must(ids => ids.Count <= DietaryAllergenAnalysisPolicy.MaxSelected)
            .WithMessage($"Choose no more than {DietaryAllergenAnalysisPolicy.MaxSelected} dietary profiles.")
            .Must(ids => ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count)
            .WithMessage("Name each dietary profile once, by its id.");
    }
}
