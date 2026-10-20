using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

/// <summary>The fixed parts of every dietary and allergen analysis (14.4, B-36). Change one, change the method version.</summary>
public static class DietaryAllergenAnalysisPolicy
{
    public const string MethodVersion = "dietary-allergen/1";

    /// <summary>The most allergens, or dietary profiles, one analysis is asked about.</summary>
    public const int MaxSelected = 20;

    public const string CrossContactDisclaimer =
        "This analysis reads reviewed ingredient records only. It cannot account for shared equipment, facilities, "
        + "or how a particular product was made, and it is not a guarantee that a dish is free of any allergen or "
        + "suitable for any person. Check every label, and ask anyone you are cooking for.";

    /// <param name="unknownLines">Lines with no reviewed record. A finding drawn from the rest says so.</param>
    public static string Statement(RecipeAllergenFinding finding, string allergen, int unknownLines = 0) => finding switch
    {
        RecipeAllergenFinding.Contains => $"Contains {allergen}, according to the cited records.{Unrecorded(unknownLines)}",
        RecipeAllergenFinding.Possible => $"May contain {allergen}: a cited source says at least one ingredient may.{Unrecorded(unknownLines)}",
        RecipeAllergenFinding.Unknown => $"Not known whether this recipe contains {allergen}: at least one ingredient has no reviewed record.",
        _ => $"{allergen} is not listed in the cited records for any ingredient. This is not a statement that the recipe is free of {allergen}.",
    };

    /// <param name="unknownLines">Lines with no reviewed record. A finding drawn from the rest says so.</param>
    public static string Statement(RecipeDietaryFinding finding, string profile, int unknownLines = 0) => finding switch
    {
        RecipeDietaryFinding.DoesNotFit => $"Does not fit {profile}, according to the cited records.{Unrecorded(unknownLines)}",
        RecipeDietaryFinding.DependsOnProduct => $"Whether this fits {profile} depends on the products used; the cited sources say what it depends on.{Unrecorded(unknownLines)}",
        RecipeDietaryFinding.Unknown => $"Not known whether this fits {profile}: at least one ingredient has no reviewed record.",

        // Never "Fits Nut-Free": for a profile named for what it leaves out, that would read as the free-from
        // claim the allergen side forbids. The records are the subject of the sentence, not the dish.
        _ => $"Every ingredient is recorded as compatible with {profile} in the cited records. A description of the ingredients, not a medical statement.",
    };

    /// <summary>
    /// Whether a source may give a reassuring word — "not listed", "compatible" — that an analysis acts on: a vetted
    /// kind, and not retired. Any approved source may give a cautious one. Restated from the Ingredients module's
    /// trait policy, which a policy type cannot cross a module boundary to share; a test holds them equal.
    /// </summary>
    public static bool MayReassure(ReferenceSourceKind kind, bool isActive) =>
        isActive && kind is not (ReferenceSourceKind.CommunityContributed or ReferenceSourceKind.AiEstimated);

    private static string Unrecorded(int lines) => lines switch
    {
        0 => string.Empty,
        1 => " One other ingredient has no reviewed record.",
        _ => $" {lines} other ingredients have no reviewed record.",
    };
}

/// <summary>A selected allergen or dietary profile, by id and the name it is shown under.</summary>
public sealed record AnalysisSubject(Guid Id, string Name);

/// <summary>
/// The dietary and allergen decision table (14.4, ING-007). Pure: the same version, selection and records are the
/// same analysis.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only what a source stated.</strong> A finding comes from approved traits and nothing else — never from
/// an ingredient's category, its neighbours, a line's own words or a model. No record is unknown, never absent.
/// </para>
/// <para>
/// <strong>The more cautious finding wins</strong>, line by line and across the recipe, and a disagreement between
/// sources is reported as a conflict with both cited rather than resolved.
/// </para>
/// </remarks>
public static class DietaryAllergenAnalyzer
{
    public static DietaryAllergenAnalysisServiceModel Analyze(
        Guid recipeId,
        Guid recipeVersionId,
        int versionNumber,
        IReadOnlyList<NutritionEstimateLineInput> lines,
        IReadOnlyList<AnalysisSubject> allergens,
        IReadOnlyList<AnalysisSubject> profiles,
        IngredientTraitEvidenceServiceModel evidence,
        DateOnly evaluatedOn)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(evidence);

        evidence = InEffectOn(evidence, evaluatedOn);

        return new DietaryAllergenAnalysisServiceModel(
            recipeId,
            recipeVersionId,
            versionNumber,
            DietaryAllergenAnalysisPolicy.MethodVersion,
            evaluatedOn,
            DietaryAllergenAnalysisPolicy.CrossContactDisclaimer,
            [.. allergens.OrderBy(subject => subject.Name, StringComparer.Ordinal).Select(subject => Allergen(subject, lines, evidence))],
            [.. profiles.OrderBy(subject => subject.Name, StringComparer.Ordinal).Select(subject => Dietary(subject, lines, evidence))],
            Sources(evidence));
    }

    /// <summary>
    /// Whether a reassuring word from this source counts, and why not (14.7, L-2 of the audit).
    /// </summary>
    /// <remarks>
    /// The two halves of <see cref="DietaryAllergenAnalysisPolicy.MayReassure"/>, told apart so the answer can
    /// say which one applied. An unvetted kind and a retired source are refused for different reasons and a
    /// creator reading the citation deserves the right one; working it out from the kind and the active flag
    /// was what every screen had been doing for itself.
    /// </remarks>
    private static TraitCountedReason ReassuranceReason(TraitSourceServiceModel source) =>
        !DietaryAllergenAnalysisPolicy.MayReassure(source.Kind, isActive: true) ? TraitCountedReason.UnvettedKind
        : !source.IsActive ? TraitCountedReason.RetiredSource
        : TraitCountedReason.Counted;

    /// <summary>
    /// The traits in effect on the evaluation date, and only those (14.7, M-2 of the audit).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repository already filters by review status and effective date, and the database test that proves a
    /// future-dated trait drives no finding goes through it. This is the same rule restated where the finding
    /// is actually made: the analyzer took <c>evaluatedOn</c>, recorded it on the answer, and never compared
    /// anything with it, so a second read path — a cache, a batch job, a test harness — that passed unfiltered
    /// traits would have produced an analysis citing records that had not taken effect.
    /// </para>
    /// <para>
    /// Approval cannot be re-checked here: an approved trait is all the facade returns, and the service model
    /// carries no review status to test. That half stays the repository's, with
    /// <c>TraitPolicy.MayBeApproved</c> now guarding what may reach it.
    /// </para>
    /// </remarks>
    private static IngredientTraitEvidenceServiceModel InEffectOn(
        IngredientTraitEvidenceServiceModel evidence, DateOnly evaluatedOn) =>
        new(
            [.. evidence.Allergens.Where(trait => trait.EffectiveFrom <= evaluatedOn)],
            [.. evidence.Dietary.Where(trait => trait.EffectiveFrom <= evaluatedOn)]);

    /// <summary>
    /// How complete the records behind one finding are. Derived from the line findings alone, so it can never move
    /// a finding: a line with a counted record is exactly a line whose finding is not <c>Unknown</c>.
    /// </summary>
    internal static (AnalysisConfidence Confidence, AnalysisCoverageServiceModel Coverage) ConfidenceOf(
        int linesRead, int linesWithCountedRecords, bool anyConflict) =>
        (linesWithCountedRecords == 0 ? AnalysisConfidence.None
            : anyConflict ? AnalysisConfidence.Disputed
            : linesWithCountedRecords < linesRead ? AnalysisConfidence.Partial
            : AnalysisConfidence.Recorded,
        new AnalysisCoverageServiceModel(linesRead, linesWithCountedRecords));

    /// <summary>
    /// Every source the analysis read a record from, once each, with its licence and the newest effective date among
    /// its records. The evidence was read for exactly the lines and subjects asked about, so this is what was cited.
    /// </summary>
    private static List<AnalysisSourceServiceModel> Sources(IngredientTraitEvidenceServiceModel evidence) =>
        [.. evidence.Allergens.Select(trait => (trait.Source, trait.EffectiveFrom))
            .Concat(evidence.Dietary.Select(trait => (trait.Source, trait.EffectiveFrom)))
            .GroupBy(entry => entry.Source.Code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var source = group.First().Source;
                return new AnalysisSourceServiceModel(
                    source.Code, source.Name, source.Kind, source.Citation, source.LicenseCode, source.LicenseUrl, source.IsActive,
                    group.Max(entry => entry.EffectiveFrom));
            })];

    // ---- Allergens ----

    private static AllergenAnalysisServiceModel Allergen(
        AnalysisSubject allergen, IReadOnlyList<NutritionEstimateLineInput> lines, IngredientTraitEvidenceServiceModel evidence)
    {
        var findings = lines.Select(line => AllergenLine(line, allergen.Id, evidence)).ToList();

        // A recipe with no lines has no records at all: unknown, not "not listed".
        var finding =
            findings.Count == 0 ? RecipeAllergenFinding.Unknown
            : findings.Any(line => line.Finding is AllergenLineFinding.Contains) ? RecipeAllergenFinding.Contains
            : findings.Any(line => line.Finding is AllergenLineFinding.Possible) ? RecipeAllergenFinding.Possible
            : findings.Any(line => line.Finding is AllergenLineFinding.Unknown) ? RecipeAllergenFinding.Unknown
            : RecipeAllergenFinding.NotListedInRecords;

        var unknown = LinesWhere(findings, line => line.Finding is AllergenLineFinding.Unknown);
        var (confidence, coverage) = ConfidenceOf(findings.Count, findings.Count - unknown.Count, findings.Any(line => line.Conflict));

        return new AllergenAnalysisServiceModel(
            allergen.Id,
            allergen.Name,
            finding,
            confidence,
            coverage,
            DietaryAllergenAnalysisPolicy.Statement(finding, allergen.Name, unknown.Count),
            LinesWhere(findings, line => line.Finding is AllergenLineFinding.Contains),
            LinesWhere(findings, line => line.Finding is AllergenLineFinding.Possible),
            unknown,
            LinesWhere(findings, line => line.Conflict),
            findings);
    }

    private static AllergenLineAnalysisServiceModel AllergenLine(
        NutritionEstimateLineInput line, Guid allergenId, IngredientTraitEvidenceServiceModel evidence)
    {
        if (line.IngredientId is not { } ingredientId)
        {
            return new(line.LineId, line.DisplayText, line.IsOptional, null, AllergenLineFinding.Unknown, TraitFindingReason.NotARecognisedIngredient, false, []);
        }

        var traits = evidence.Allergens
            .Where(trait => trait.IngredientId == ingredientId && trait.AllergenId == allergenId)
            .OrderBy(trait => trait.Source.Code, StringComparer.Ordinal)
            .ToList();

        // A source that was consulted and does not say is no more than no source at all, and "not listed" counts
        // only from a source that may reassure. The database already refuses it from an unvetted kind; this is the
        // second guard, and the one that also covers a retired source.
        var counted = traits.Select(trait => (Trait: trait, Reason: trait.Presence switch
        {
            AllergenPresence.Unknown => TraitCountedReason.SourceDoesNotSay,
            AllergenPresence.NotListedBySource => ReassuranceReason(trait.Source),
            _ => TraitCountedReason.Counted,
        }))
            .Select(entry => (entry.Trait, Counted: entry.Reason is TraitCountedReason.Counted, entry.Reason))
            .ToList();
        var states = counted.Where(entry => entry.Counted).Select(entry => entry.Trait.Presence).ToHashSet();

        var finding =
            states.Contains(AllergenPresence.Present) ? AllergenLineFinding.Contains
            : states.Contains(AllergenPresence.PossiblePresence) ? AllergenLineFinding.Possible
            : states.Contains(AllergenPresence.NotListedBySource) ? AllergenLineFinding.NotListed
            : AllergenLineFinding.Unknown;

        // One source lists it as there or possibly there, another publishes a composition without it.
        var conflict = states.Contains(AllergenPresence.NotListedBySource)
            && (states.Contains(AllergenPresence.Present) || states.Contains(AllergenPresence.PossiblePresence));

        return new(
            line.LineId,
            line.DisplayText,
            line.IsOptional,
            ingredientId,
            finding,
            finding is AllergenLineFinding.Unknown ? TraitFindingReason.NoRecord : TraitFindingReason.None,
            conflict,
            [.. counted.Select(entry => new AllergenTraitEvidenceServiceModel(
                entry.Trait.Presence, entry.Trait.Source.Code, entry.Trait.Source.Name, entry.Trait.Source.Kind, entry.Trait.Source.Citation,
                entry.Trait.EffectiveFrom, entry.Trait.EvidenceNote, entry.Counted, entry.Reason))]);
    }

    // ---- Dietary profiles ----

    private static DietaryAnalysisServiceModel Dietary(
        AnalysisSubject profile, IReadOnlyList<NutritionEstimateLineInput> lines, IngredientTraitEvidenceServiceModel evidence)
    {
        var findings = lines.Select(line => DietaryLine(line, profile.Id, evidence)).ToList();

        var finding =
            findings.Count == 0 ? RecipeDietaryFinding.Unknown
            : findings.Any(line => line.Finding is DietaryLineFinding.Incompatible) ? RecipeDietaryFinding.DoesNotFit
            : findings.Any(line => line.Finding is DietaryLineFinding.DependsOnProduct) ? RecipeDietaryFinding.DependsOnProduct
            : findings.Any(line => line.Finding is DietaryLineFinding.Unknown) ? RecipeDietaryFinding.Unknown
            : RecipeDietaryFinding.FitsAsRecorded;

        var unknown = LinesWhere(findings, line => line.Finding is DietaryLineFinding.Unknown);
        var (confidence, coverage) = ConfidenceOf(findings.Count, findings.Count - unknown.Count, findings.Any(line => line.Conflict));

        return new DietaryAnalysisServiceModel(
            profile.Id,
            profile.Name,
            finding,
            confidence,
            coverage,
            DietaryAllergenAnalysisPolicy.Statement(finding, profile.Name, unknown.Count),
            LinesWhere(findings, line => line.Finding is DietaryLineFinding.Incompatible),
            LinesWhere(findings, line => line.Finding is DietaryLineFinding.DependsOnProduct),
            unknown,
            LinesWhere(findings, line => line.Conflict),
            findings);
    }

    private static DietaryLineAnalysisServiceModel DietaryLine(
        NutritionEstimateLineInput line, Guid profileId, IngredientTraitEvidenceServiceModel evidence)
    {
        if (line.IngredientId is not { } ingredientId)
        {
            return new(line.LineId, line.DisplayText, line.IsOptional, null, DietaryLineFinding.Unknown, TraitFindingReason.NotARecognisedIngredient, false, []);
        }

        var traits = evidence.Dietary
            .Where(trait => trait.IngredientId == ingredientId && trait.DietaryProfileId == profileId)
            .OrderBy(trait => trait.Source.Code, StringComparer.Ordinal)
            .ToList();

        // "Compatible" counts only from a source that may reassure; there is no database guard for it, so this is
        // the only one.
        var counted = traits.Select(trait => (Trait: trait, Reason: trait.Compatibility switch
        {
            DietaryCompatibility.Unknown => TraitCountedReason.SourceDoesNotSay,
            DietaryCompatibility.Compatible => ReassuranceReason(trait.Source),
            _ => TraitCountedReason.Counted,
        }))
            .Select(entry => (entry.Trait, Counted: entry.Reason is TraitCountedReason.Counted, entry.Reason))
            .ToList();
        var states = counted.Where(entry => entry.Counted).Select(entry => entry.Trait.Compatibility).ToHashSet();

        var finding =
            states.Contains(DietaryCompatibility.Incompatible) ? DietaryLineFinding.Incompatible
            : states.Contains(DietaryCompatibility.DependsOnProduct) ? DietaryLineFinding.DependsOnProduct
            : states.Contains(DietaryCompatibility.Compatible) ? DietaryLineFinding.Compatible
            : DietaryLineFinding.Unknown;

        // One source says it fits, another that it does not or that it depends.
        var conflict = states.Contains(DietaryCompatibility.Compatible)
            && (states.Contains(DietaryCompatibility.Incompatible) || states.Contains(DietaryCompatibility.DependsOnProduct));

        return new(
            line.LineId,
            line.DisplayText,
            line.IsOptional,
            ingredientId,
            finding,
            finding is DietaryLineFinding.Unknown ? TraitFindingReason.NoRecord : TraitFindingReason.None,
            conflict,
            [.. counted.Select(entry => new DietaryTraitEvidenceServiceModel(
                entry.Trait.Compatibility, entry.Trait.Source.Code, entry.Trait.Source.Name, entry.Trait.Source.Kind, entry.Trait.Source.Citation,
                entry.Trait.EffectiveFrom, entry.Trait.EvidenceNote, entry.Counted, entry.Reason))]);
    }

    private static List<Guid> LinesWhere<T>(IEnumerable<T> findings, Func<T, bool> predicate)
        where T : notnull =>
        [.. findings.Where(predicate).Select(finding => finding switch
        {
            AllergenLineAnalysisServiceModel allergen => allergen.LineId,
            DietaryLineAnalysisServiceModel dietary => dietary.LineId,
            _ => throw new ArgumentException("Not a line finding.", nameof(findings)),
        })];
}
