namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract AIREC-006's answer must satisfy: field-linked findings about one pinned recipe version, each
/// carrying a category, a severity, evidence, and whether it needs checking against a reference the system
/// does not have. This type <em>is</em> the schema — <see cref="AiRecipeReviewOutputSchema"/> exports it for
/// the prompt — and the answer is held to it by strict deserialization, the same guarantee
/// <see cref="AiSubstitutionOutputDocument"/> makes for AIREC-004.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A finding is a review signal, never a certification and never an edit.</strong> Nothing in this
/// document addresses a change to the recipe it reviews, and the rows it is stored as carry
/// <see cref="AiChangeTargetKind.RecipeReviewFinding"/>, which neither <see cref="AiChangeApplicability"/> nor
/// <see cref="AiChangeTargetPolicy"/> covers — so no accepted finding can be translated into a recipe edit. A
/// creator acts on a finding by editing the recipe themselves, or by deciding it does not apply.
/// </para>
/// <para>
/// The operation also runs in <see cref="AiOperationScope.Advisory"/>, which permits no target at all — the
/// same second fact <see cref="AiSubstitutionOutputDocument"/> records for AIREC-004, for the same reason: this
/// capability has its own validator, which never asks <see cref="AiPolicy.AllowedTargets"/>.
/// </para>
/// <para>
/// <strong>High-risk methods and unsupported claims are structured, not left to prose discipline.</strong>
/// <see cref="AiRecipeReviewFinding.RequiresReferenceCheck"/> has a floor the validator enforces — every
/// allergen or dietary conflict, and every critical finding, must declare it needs checking against a source
/// this system does not yet have (14.5) — and <see cref="AiRecipeReviewFinding.UnknownFactors"/> is required
/// non-empty on an <see cref="AiRecipeReviewFindingCategory.UnsupportedClaim"/> finding, so "this is unverified"
/// cannot stand alone without saying what is unverified.
/// </para>
/// <para>
/// Nothing here is persisted verbatim as a unit. <see cref="RecipeReviewAiTaskHandler"/> flattens a validated
/// document into <c>AiStructuredChange</c> and <c>AiWarning</c> rows against a server-minted id per finding,
/// the same storage <see cref="AiSubstitutionOutputDocument"/> uses, and never <see cref="AiDiffCalculator"/> —
/// there is no proposed change to resolve against the pinned version.
/// </para>
/// </remarks>
public sealed record AiRecipeReviewOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// The findings, in no required order. May be empty: "this recipe reads as complete and consistent" is a
    /// real and correct answer.
    /// </summary>
    /// <remarks>
    /// An empty list must explain itself in a warning; see <see cref="AiOutputReason.ReviewAnswerUnexplained"/>.
    /// </remarks>
    public IReadOnlyList<AiRecipeReviewFinding> Findings { get; init; } = [];

    /// <summary>What the creator should be told alongside the findings, including assumptions.</summary>
    public IReadOnlyList<AiRecipeReviewOutputWarning> Warnings { get; init; } = [];
}

/// <summary>One field-linked observation about the recipe.</summary>
public sealed record AiRecipeReviewFinding
{
    /// <summary>Which field or line this finding is about, or the recipe as a whole.</summary>
    public required AiRecipeReviewFieldRef FieldRef { get; init; }

    public required AiRecipeReviewFindingCategory Category { get; init; }

    public required AiRecipeReviewSeverity Severity { get; init; }

    /// <summary>The finding itself, in one or two sentences a creator can act on.</summary>
    public required string Summary { get; init; }

    /// <summary>Where this rests: on nothing in particular, on the model's own knowledge, or on the recipe itself.</summary>
    /// <inheritdoc cref="AiEvidenceBasis"/>
    public required AiEvidenceBasis EvidenceBasis { get; init; }

    /// <summary>What the basis actually was, when there is something specific to say. Never a citation.</summary>
    /// <inheritdoc cref="AiIngredientSubstitution.EvidenceNote"/>
    public string? EvidenceNote { get; init; }

    /// <summary>
    /// How sure the model is, on a scale a reader cannot mistake for a measurement.
    /// </summary>
    /// <inheritdoc cref="AiSubstitutionConfidence"/>
    public required AiRecipeReviewConfidence Confidence { get; init; }

    /// <summary>
    /// The allergen this finding is about, in ordinary words, when <see cref="Category"/> is
    /// <see cref="AiRecipeReviewFindingCategory.AllergenConflict"/>. Null on every other category.
    /// </summary>
    /// <inheritdoc cref="AiAllergenEffect.Allergen"/>
    public string? Allergen { get; init; }

    /// <summary>
    /// What this finding says about that allergen, required exactly when <see cref="Category"/> is
    /// <see cref="AiRecipeReviewFindingCategory.AllergenConflict"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The reason a free-text summary alone cannot carry this claim.</strong> AIREC-004 keeps "this
    /// ingredient is safe for a peanut allergy" unwritable by giving <see cref="AiAllergenEffectKind"/> no
    /// <c>Removes</c> member; a finding whose category names an allergen conflict but leaves the claim itself
    /// in free-text prose would reopen exactly that channel. This field, and <see cref="AiAllergenEffectKind"/>
    /// itself, are what close it here too — see that type's own remarks for why the opposite member does not
    /// exist and will not be added.
    /// </para>
    /// </remarks>
    public AiAllergenEffectKind? AllergenEffect { get; init; }

    /// <summary>
    /// The diet this finding is about, in ordinary words, when <see cref="Category"/> is
    /// <see cref="AiRecipeReviewFindingCategory.DietaryConflict"/>. Null on every other category.
    /// </summary>
    /// <inheritdoc cref="AiDietaryEffect.Diet"/>
    public string? Diet { get; init; }

    /// <summary>
    /// What this finding says about that diet, required exactly when <see cref="Category"/> is
    /// <see cref="AiRecipeReviewFindingCategory.DietaryConflict"/>.
    /// </summary>
    /// <inheritdoc cref="AllergenEffect"/>
    public AiDietaryEffectKind? DietaryEffect { get; init; }

    /// <summary>
    /// Whether this finding needs checking against a vetted reference before a creator relies on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared on every finding rather than inferred, and held to a floor the validator enforces: it must be
    /// <c>true</c> whenever <see cref="Category"/> is <see cref="AiRecipeReviewFindingCategory.AllergenConflict"/>
    /// or <see cref="AiRecipeReviewFindingCategory.DietaryConflict"/>, or <see cref="Severity"/> is
    /// <see cref="AiRecipeReviewSeverity.Major"/> or <see cref="AiRecipeReviewSeverity.Critical"/> — see
    /// <see cref="AiOutputReason.ReferenceCheckRequired"/>. A finding that sets this also owes a
    /// <see cref="AiWarningKind.SafetyCaution"/> warning beside it, regardless of category — see
    /// <see cref="AiOutputReason.SafetyFindingUncautioned"/>.
    /// </para>
    /// <para>
    /// <strong>This records the need; it does not perform the check.</strong> There is no vetted reference
    /// corpus behind this capability yet (14.5), so <c>true</c> here means "a creator should verify this against
    /// a source before relying on it," never "this has been verified." A finding with this set to
    /// <c>false</c> has not been found safe to skip checking — it has simply not been flagged as needing one.
    /// </para>
    /// </remarks>
    public required bool RequiresReferenceCheck { get; init; }

    /// <summary>
    /// What is unknown, when <see cref="Category"/> is <see cref="AiRecipeReviewFindingCategory.UnsupportedClaim"/>.
    /// </summary>
    /// <remarks>
    /// Required non-empty on that category and meaningless on every other — AIREC-006's restriction is that an
    /// unsupported claim must say what is unknown, not merely be labelled as one. See
    /// <see cref="AiOutputReason.UnsupportedClaimUnexplained"/>.
    /// </remarks>
    public IReadOnlyList<string> UnknownFactors { get; init; } = [];
}

/// <summary>What a finding is about: the recipe as a whole, one of its scalar fields, or one of its lines.</summary>
/// <remarks>
/// A field reference is structured rather than a path string, for the reason <see cref="AiChangeTargetKind"/>'s
/// own remarks give: an enum can be checked before anything is built from it, where a free-text path would make
/// that check string matching over a value a model wrote.
/// </remarks>
public sealed record AiRecipeReviewFieldRef
{
    public required AiRecipeReviewFieldKind FieldKind { get; init; }

    /// <summary>
    /// The stable id of the ingredient line, step, or equipment item this finding is about, read from the
    /// SOURCE segment's own ids.
    /// </summary>
    /// <remarks>
    /// Required when <see cref="FieldKind"/> is <see cref="AiRecipeReviewFieldKind.IngredientLine"/>,
    /// <see cref="AiRecipeReviewFieldKind.Step"/>, or <see cref="AiRecipeReviewFieldKind.Equipment"/>, which
    /// have a child to name; null for every other field kind, which does not. The validator checks the shape of
    /// that pairing; <see cref="RecipeReviewAiTaskHandler"/> checks afterward, against the pinned snapshot, that
    /// an id naming a child actually names one in it — the same check <see cref="AiDiffCalculator"/> makes for
    /// an ordinary diff's target row.
    /// </remarks>
    public Guid? EntityId { get; init; }
}

/// <summary>What kind of observation a finding is making about the recipe.</summary>
public enum AiRecipeReviewFindingCategory
{
    /// <summary>Not declared. Rejected by the validator; an observation nobody categorised is not one.</summary>
    Unspecified = 0,

    /// <summary>Something a complete recipe needs is missing — a time, a yield, a step, an ingredient quantity.</summary>
    Completeness = 1,

    /// <summary>Two parts of the recipe disagree — an ingredient named in the list but not the method, or the reverse.</summary>
    Consistency = 2,

    /// <summary>A prep, cook, rest, or total time that looks wrong for what the method describes.</summary>
    Timing = 3,

    /// <summary>An oven, stovetop, or internal temperature that looks wrong, missing, or worth double-checking.</summary>
    Temperature = 4,

    /// <summary>A step whose instruction could reasonably be read more than one way.</summary>
    AmbiguousStep = 5,

    /// <summary>An ingredient listed but never referenced by any step.</summary>
    UnusedIngredient = 6,

    /// <summary>Something about the method that looks likely to fail as written, independent of safety.</summary>
    LikelyFailure = 7,

    /// <summary>An ingredient or technique that introduces or may introduce a named allergen.</summary>
    AllergenConflict = 8,

    /// <summary>An ingredient or technique that conflicts or may conflict with a named diet.</summary>
    DietaryConflict = 9,

    /// <summary>
    /// A claim in the recipe's own text — a headnote, a note, a step — that is not supported by anything this
    /// system can check.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="AiRecipeReviewFinding.UnknownFactors"/> non-empty; see
    /// <see cref="AiOutputReason.UnsupportedClaimUnexplained"/>.
    /// </remarks>
    UnsupportedClaim = 10,
}

/// <summary>How much a finding matters, on a scale a reader cannot mistake for a computed score.</summary>
public enum AiRecipeReviewSeverity
{
    /// <summary>Not declared. Rejected by the validator rather than read as any particular level.</summary>
    Unspecified = 0,

    /// <summary>Worth knowing; nothing to act on unless the creator wants to.</summary>
    Info = 1,

    /// <summary>A small polish — a missing detail, a word that could be clearer.</summary>
    Minor = 2,

    /// <summary>Worth the creator's attention before publishing, though the recipe would still work.</summary>
    Moderate = 3,

    /// <summary>Likely to cause a real problem for someone who follows the recipe as written.</summary>
    Major = 4,

    /// <summary>
    /// A safety-relevant or method-breaking problem. Every <see cref="AiRecipeReviewFindingCategory.AllergenConflict"/>
    /// and <see cref="AiRecipeReviewFindingCategory.DietaryConflict"/> finding is held to at least
    /// <see cref="Major"/>, and this level always requires <see cref="AiRecipeReviewFinding.RequiresReferenceCheck"/>.
    /// </summary>
    Critical = 5,
}

/// <summary>Which part of the recipe a finding's <see cref="AiRecipeReviewFieldRef"/> names.</summary>
public enum AiRecipeReviewFieldKind
{
    /// <summary>Not declared. Rejected by the validator; a finding about nothing in particular is not linked.</summary>
    Unspecified = 0,

    /// <summary>The recipe as a whole — not any one field.</summary>
    Recipe = 1,

    Title = 2,

    Headnote = 3,

    Yield = 4,

    PrepTime = 5,

    CookTime = 6,

    RestTime = 7,

    TotalTime = 8,

    /// <summary>One ingredient line, named by <see cref="AiRecipeReviewFieldRef.EntityId"/>.</summary>
    IngredientLine = 9,

    /// <summary>One instruction step, named by <see cref="AiRecipeReviewFieldRef.EntityId"/>.</summary>
    Step = 10,

    /// <summary>One equipment item, named by <see cref="AiRecipeReviewFieldRef.EntityId"/>.</summary>
    Equipment = 11,

    /// <summary>The recipe's working notes.</summary>
    Note = 12,

    /// <summary>The recipe's storage guidance.</summary>
    Storage = 13,
}

/// <summary>How sure the model is about one finding.</summary>
/// <remarks>
/// Three named levels rather than a number, for the reason <see cref="AiSubstitutionConfidence"/> gives:
/// <c>0.87</c> reads as something that was computed, and nothing computed it.
/// </remarks>
public enum AiRecipeReviewConfidence
{
    /// <summary>Not declared. Rejected by the validator rather than read as any particular level.</summary>
    Unspecified = 0,

    /// <summary>Worth flagging, and genuinely uncertain.</summary>
    Low = 1,

    /// <summary>A plausible reading of the recipe, though not the only one.</summary>
    Moderate = 2,

    /// <summary>Clearly supported by what the recipe itself says.</summary>
    High = 3,
}

/// <summary>One thing to tell the creator about the answer as a whole, or about one finding.</summary>
public sealed record AiRecipeReviewOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// The finding this is about, as an index into <see cref="AiRecipeReviewOutputDocument.Findings"/>. Null
    /// for a warning about the answer as a whole — which is what an answer proposing no finding must carry.
    /// </summary>
    public int? FindingIndex { get; init; }
}
