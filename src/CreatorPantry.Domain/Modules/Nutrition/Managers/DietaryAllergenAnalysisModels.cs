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
public sealed record AllergenLineFindingServiceModel(
    Guid LineId,
    string DisplayText,
    bool IsOptional,
    Guid? IngredientId,
    AllergenLineFinding Finding,
    TraitFindingReason Reason,
    bool Conflict,
    IReadOnlyList<AllergenTraitEvidenceServiceModel> Evidence);

public sealed record DietaryLineFindingServiceModel(
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
    string Statement,
    IReadOnlyList<Guid> ContainingLines,
    IReadOnlyList<Guid> PossibleSources,
    IReadOnlyList<Guid> UnknownLines,
    IReadOnlyList<Guid> ConflictingLines,
    IReadOnlyList<AllergenLineFindingServiceModel> Lines);

/// <summary>One selected dietary profile across the recipe.</summary>
/// <param name="DependsOnProductLines">Lines whose fit varies by product: the hidden sources the records know of.</param>
public sealed record DietaryAnalysisServiceModel(
    Guid DietaryProfileId,
    string DietaryProfileName,
    RecipeDietaryFinding Finding,
    string Statement,
    IReadOnlyList<Guid> IncompatibleLines,
    IReadOnlyList<Guid> DependsOnProductLines,
    IReadOnlyList<Guid> UnknownLines,
    IReadOnlyList<Guid> ConflictingLines,
    IReadOnlyList<DietaryLineFindingServiceModel> Lines);

/// <summary>
/// A deterministic dietary and allergen analysis of one recipe version against selected profiles (14.4, ING-007).
/// A reading of approved ingredient records — never a certification, and never a statement about a person.
/// </summary>
/// <param name="EvaluatedOn">The date the records were read as of (UTC). A trait dated later was not yet in effect.</param>
/// <param name="CrossContactDisclaimer">On every analysis, whatever it found.</param>
public sealed record DietaryAllergenAnalysisServiceModel(
    Guid RecipeId,
    Guid RecipeVersionId,
    int VersionNumber,
    string MethodVersion,
    DateOnly EvaluatedOn,
    string CrossContactDisclaimer,
    IReadOnlyList<AllergenAnalysisServiceModel> Allergens,
    IReadOnlyList<DietaryAnalysisServiceModel> Dietary);

// ---- What is asked ----

/// <summary>The allergens and dietary profiles to analyse a recipe version against.</summary>
public sealed class DietaryAllergenAnalysisViewModel
{
    public List<Guid> AllergenIds { get; set; } = [];

    public List<Guid> DietaryProfileIds { get; set; } = [];
}

internal sealed class DietaryAllergenAnalysisViewModelValidator : AbstractValidator<DietaryAllergenAnalysisViewModel>
{
    public DietaryAllergenAnalysisViewModelValidator()
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
