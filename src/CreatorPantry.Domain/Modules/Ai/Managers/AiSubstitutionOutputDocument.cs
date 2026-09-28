namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract AIREC-004's answer must satisfy: ranked alternatives for one ingredient the creator selected,
/// with what each one does to the dish and what has to be checked before trusting it. This type <em>is</em> the
/// schema — <see cref="AiSubstitutionOutputSchema"/> exports it for the prompt — and the answer is held to it
/// by strict deserialization, the same guarantee <see cref="AiOutputDocument"/> makes for a recipe diff.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Advice, never an edit.</strong> Nothing in this document addresses a change to the recipe it was
/// asked about, and the rows it is stored as carry <see cref="AiChangeTargetKind.IngredientSubstitution"/>,
/// which neither <see cref="AiChangeApplicability"/> nor <see cref="AiChangeTargetPolicy"/> covers — so no
/// accepted change can be translated into a recipe edit. A creator acts on this by editing the recipe
/// themselves.
/// </para>
/// <para>
/// The operation also runs in <see cref="AiOperationScope.Advisory"/>, which permits no target at all. That
/// is a second fact rather than a second mechanism: this capability has its own validator, which never asks
/// <see cref="AiPolicy.AllowedTargets"/>, so the scope column records the bound for anything reading the
/// operation later without being the thing that enforces it here. The enforcement is the target kind.
/// </para>
/// <para>
/// <strong>Culinary plausibility and allergen safety are different claims, and this type keeps them
/// apart.</strong> <see cref="AiIngredientSubstitution.FlavorImpact"/> and its siblings are judgements about
/// how a dish will behave. <see cref="AiIngredientSubstitution.AllergenEffects"/> and
/// <see cref="AiIngredientSubstitution.DietaryEffects"/> are structured statements about what an alternative
/// brings with it, and both run one way only: something may be introduced or may conflict, and nothing can
/// ever be declared removed or satisfied. See <see cref="AiAllergenEffectKind"/> and
/// <see cref="AiDietaryEffectKind"/> for why the opposite members do not exist.
/// </para>
/// <para>
/// <strong>What that does and does not buy, stated plainly.</strong> Every field this document has for saying
/// something about an allergen or a diet is structured, so the dangerous claim has no designed channel and no
/// model can make it by filling in the form. What remains is prose: <see cref="AiIngredientSubstitution"/>'s
/// impact notes, its evidence note, and a warning's message are free text, held only to a length bound and a
/// control-character scan, and a sentence asserting that something is safe for someone fits in any of them.
/// The prompt forbids it and the evaluation set probes it, but neither is a guarantee the way an absent enum
/// member is. Screening prose for assertions is real work this document does not do — the honest summary is
/// that the structured surface is closed and the prose surface is narrowed, not that the claim is impossible.
/// </para>
/// <para>
/// <strong>Nothing here is persisted verbatim as a unit.</strong> <see cref="IngredientSubstitutionAiTaskHandler"/>
/// flattens a validated document into <c>AiStructuredChange</c> and <c>AiWarning</c> rows against a
/// server-minted id per alternative, the same storage every other capability uses, and never
/// <see cref="AiDiffCalculator"/> — there is no proposed change to resolve against the pinned version.
/// </para>
/// </remarks>
public sealed record AiSubstitutionOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// The alternatives, best first. Empty is a valid and sometimes correct answer.
    /// </summary>
    /// <remarks>
    /// An ingredient can be load-bearing — the acid a set depends on, the sugar a preserve keeps by — and for
    /// one of those the honest answer is that there is no swap worth offering. An empty list must explain
    /// itself in a warning; see <see cref="AiOutputReason.SubstitutionAnswerUnexplained"/>.
    /// </remarks>
    public IReadOnlyList<AiIngredientSubstitution> Substitutions { get; init; } = [];

    /// <summary>What the creator should be told alongside the alternatives, including cautions and assumptions.</summary>
    public IReadOnlyList<AiSubstitutionOutputWarning> Warnings { get; init; } = [];
}

/// <summary>One candidate replacement for the selected ingredient: a starting point, never an equivalence.</summary>
public sealed record AiIngredientSubstitution
{
    /// <summary>
    /// Where this sits among the alternatives, best first.
    /// </summary>
    /// <remarks>
    /// The ranks across one answer must be exactly <c>1..N</c> with no gaps and no ties. "Ranked alternatives"
    /// is what AIREC-004 asks for, and a list where two options both claim second place has not ranked them.
    /// </remarks>
    public required int Rank { get; init; }

    /// <summary>The alternative, in the words a creator would write on their own ingredient line.</summary>
    public required string Alternative { get; init; }

    /// <summary>
    /// What the original ingredient is doing <em>in this recipe</em> — binder, acid, fat, leavener, bulk,
    /// browning, moisture — which is what decides whether anything can stand in for it.
    /// </summary>
    /// <remarks>
    /// Required, because it is the reasoning the rest of the entry rests on. An alternative offered without it
    /// is a guess about an ingredient rather than about the dish, and a creator cannot tell the two apart.
    /// </remarks>
    public required string FunctionalRole { get; init; }

    /// <summary>
    /// How much to use, in prose: "start with the same weight", "use about three quarters and taste".
    /// </summary>
    /// <remarks>
    /// <strong>Prose, and there is deliberately no numeric field beside it.</strong> A multiplier invites the
    /// system to do arithmetic with a model's number, and ai.md routes scaling and conversion to deterministic
    /// domain code. A figure a creator reads and applies themselves is a suggestion; one the system multiplies
    /// by is a calculation nothing verified.
    /// </remarks>
    public required string QuantityGuidance { get; init; }

    /// <summary>What has to be done differently — an extra rest, a lower oven, a longer whisk.</summary>
    public string? TechniqueImpact { get; init; }

    /// <summary>How the dish will taste different.</summary>
    public string? FlavorImpact { get; init; }

    /// <summary>How the dish will feel different — crumb, set, chew, body.</summary>
    public string? TextureImpact { get; init; }

    /// <summary>
    /// What this alternative does about a diet the creator might be cooking for, diet by diet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Structured for the reason <see cref="AllergenEffects"/> is, and it was free text first.</strong>
    /// A prose dietary note is a channel for exactly the claim this capability must not make: "oat milk is
    /// dairy-free, so this is safe for your reader's milk allergy" is a sentence, and no length bound or
    /// control-character scan can tell it from "slightly sweeter". Worse, it bypassed the rule that requires a
    /// caution — that rule keys off a structured list, and a model stating the claim in prose left the list
    /// empty and owed nothing.
    /// </para>
    /// <para>
    /// So dietary claims run one way too. See <see cref="AiDietaryEffectKind"/>: an alternative can be said to
    /// conflict with a diet, to possibly conflict, or to be unknown. It can never be said to satisfy one.
    /// </para>
    /// </remarks>
    public IReadOnlyList<AiDietaryEffect> DietaryEffects { get; init; } = [];

    /// <summary>
    /// What this alternative brings with it, allergen by allergen.
    /// </summary>
    /// <remarks>
    /// Structured rather than prose, so the one claim that must never be made cannot be written down. Any
    /// entry here — or in <see cref="DietaryEffects"/> — obliges a
    /// <see cref="AiWarningKind.SafetyCaution"/> addressed to this substitution; see
    /// <see cref="AiOutputReason.AllergenEffectUncautioned"/>.
    /// </remarks>
    public IReadOnlyList<AiAllergenEffect> AllergenEffects { get; init; } = [];

    /// <summary>
    /// How sure the model is, on a scale a reader cannot mistake for a measurement.
    /// </summary>
    /// <remarks>
    /// Three named levels rather than a number. <c>0.87</c> reads as something that was computed, and nothing
    /// computed it — it would be a feeling wearing the clothes of a statistic.
    /// </remarks>
    public required AiSubstitutionConfidence Confidence { get; init; }

    /// <summary>Where this rests: on nothing in particular, on the model's own knowledge, or on the recipe itself.</summary>
    public required AiEvidenceBasis EvidenceBasis { get; init; }

    /// <summary>What the basis actually was, when there is something specific to say.</summary>
    /// <remarks>
    /// Never a citation. There is no vetted reference corpus behind this capability yet, so a note here is
    /// context for a creator's own judgement and not a source anything checked — which is exactly why
    /// <see cref="AiEvidenceBasis"/> has no member that would let it be presented as one.
    /// </remarks>
    public string? EvidenceNote { get; init; }

    /// <summary>
    /// What to make, taste, or measure before relying on this — a half batch, a single test muffin, a set
    /// checked cold.
    /// </summary>
    /// <remarks>
    /// <strong>Required on every alternative, with no exception and no way to opt out.</strong> "No guaranteed
    /// equivalence" is AIREC-004's restriction, and this is the shape it takes in the type system: an
    /// alternative that needs no testing is not expressible, so the document cannot describe a swap as settled
    /// even when a model is confident it is.
    /// </remarks>
    public required string TestRecommendation { get; init; }
}

/// <summary>One allergen consequence of choosing an alternative.</summary>
public sealed record AiAllergenEffect
{
    /// <summary>The allergen in ordinary words: "tree nuts", "soy", "wheat".</summary>
    /// <remarks>
    /// Free text rather than a vocabulary id. The platform's <c>Allergen</c> reference table exists, but a
    /// model naming a <c>Guid</c> is a model inventing one — every capability in this module keeps vocabulary
    /// identifiers out of a model's reach for that reason. Resolving this text against the vocabulary is
    /// deterministic work for the analysis endpoint (14.6), not for a proposal.
    /// </remarks>
    public required string Allergen { get; init; }

    public required AiAllergenEffectKind Effect { get; init; }
}

/// <summary>
/// What an alternative does about one allergen.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no <c>Removes</c> member and no <c>Free</c> member, and there will not be.</strong>
/// Allergen absence cannot be established from an ingredient's name: it depends on the brand, the facility,
/// the rest of the recipe, and on cross-contact nothing here can see. <c>recipes.md</c> states it plainly —
/// do not infer allergen absence from missing data — and a creator reading "removes dairy" would take it as
/// the guarantee <c>ai.md</c> forbids. A model that emits one fails shape validation as an undefined enum
/// value rather than having the claim quietly dropped, which is the difference between a refusal the creator
/// hears about and a silent edit to what the model said.
/// </para>
/// <para>
/// So the direction is one-way on purpose. This enum can warn that an alternative brings something in. It
/// cannot say that anything has been taken out.
/// </para>
/// </remarks>
public enum AiAllergenEffectKind
{
    /// <summary>Not declared. Rejected by the validator; a consequence nobody named is not a consequence.</summary>
    Unspecified = 0,

    /// <summary>The alternative contains this allergen, on the ordinary reading of what it is.</summary>
    Introduces = 1,

    /// <summary>
    /// It may, depending on the brand, the preparation, or shared equipment. The honest answer for anything
    /// processed, and the one a creator has to check on a label rather than take from here.
    /// </summary>
    MayIntroduce = 2,

    /// <summary>Not known. Stays unknown; see <see cref="AiEvidenceBasis.Unknown"/>.</summary>
    Unknown = 3,
}

/// <summary>One consequence of choosing an alternative for a diet the creator might be cooking for.</summary>
public sealed record AiDietaryEffect
{
    /// <summary>The diet in ordinary words: "vegan", "gluten-free", "kosher".</summary>
    /// <inheritdoc cref="AiAllergenEffect.Allergen" path="/remarks"/>
    public required string Diet { get; init; }

    public required AiDietaryEffectKind Effect { get; init; }
}

/// <summary>
/// What an alternative does about one diet.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no <c>Satisfies</c> member and no <c>Suitable</c> member</strong>, for the reason
/// <see cref="AiAllergenEffectKind"/> has no <c>Removes</c>. Whether a dish meets someone's diet depends on
/// the rest of the recipe, on the brands used, on how strictly that person keeps it, and on facts this system
/// does not hold — and for a diet kept for medical or religious reasons, "this makes it vegan" read as a
/// verdict is exactly the guarantee <c>ai.md</c> forbids.
/// </para>
/// <para>
/// One direction, like its allergen counterpart. This can warn that an alternative is a problem for a diet.
/// It cannot certify that anything is fine for one.
/// </para>
/// </remarks>
public enum AiDietaryEffectKind
{
    /// <summary>Not declared. Rejected by the validator; a consequence nobody named is not a consequence.</summary>
    Unspecified = 0,

    /// <summary>The alternative is a problem for this diet, on the ordinary reading of what it is.</summary>
    Conflicts = 1,

    /// <summary>
    /// It may, depending on the brand, the processing, or how strictly the diet is kept. The honest answer
    /// wherever the answer is a label rather than a fact about the ingredient.
    /// </summary>
    MayConflict = 2,

    /// <summary>Not known. Stays unknown; see <see cref="AiEvidenceBasis.Unknown"/>.</summary>
    Unknown = 3,
}

/// <summary>How sure the model is about one alternative.</summary>
public enum AiSubstitutionConfidence
{
    /// <summary>Not declared. Rejected by the validator rather than read as any particular level.</summary>
    Unspecified = 0,

    /// <summary>Worth trying, and genuinely uncertain. The right level for anything untested.</summary>
    Low = 1,

    /// <summary>Commonly done, with outcomes that vary by recipe.</summary>
    Moderate = 2,

    /// <summary>
    /// Well established. Requires evidence better than <see cref="AiEvidenceBasis.Unknown"/>; see
    /// <see cref="AiOutputReason.ConfidenceUnevidenced"/>.
    /// </summary>
    High = 3,
}

/// <summary>
/// What an alternative's guidance rests on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no <c>VettedReference</c> member, because there is nothing yet for one to refer to.</strong>
/// The reference-fact corpus and the approved-source checks are later work (14.5). Until they exist, a member
/// naming a vetted source would let a model assert a citation that nothing in this system could check and no
/// reader could follow — a fabricated provenance, which is worse than none. When that corpus lands, a member
/// added here will mean something.
/// </para>
/// <para>
/// Until then the honest range is narrow, and deliberately so: the model's own general knowledge, something
/// the creator's own recipe already said, or nothing in particular.
/// </para>
/// </remarks>
public enum AiEvidenceBasis
{
    /// <summary>Not declared. Rejected by the validator; evidence nobody characterised is not evidence.</summary>
    Unspecified = 0,

    /// <summary>
    /// Nothing in particular stands behind this. A real and useful answer — it is what makes a low-confidence
    /// alternative worth offering at all — and it must stay this rather than being firmed up on re-reading.
    /// </summary>
    Unknown = 1,

    /// <summary>General culinary knowledge, unattributed and unchecked. Not a source.</summary>
    ModelKnowledge = 2,

    /// <summary>The creator's own recipe said so — a headnote that already suggests the swap, or a note beside the line.</summary>
    CreatorSource = 3,
}

/// <summary>One thing to tell the creator about the answer as a whole, or about one alternative.</summary>
public sealed record AiSubstitutionOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>
    /// The alternative this is about, as an index into
    /// <see cref="AiSubstitutionOutputDocument.Substitutions"/>. Null for a warning about the answer as a whole
    /// — which is what an answer proposing nothing must carry.
    /// </summary>
    public int? SubstitutionIndex { get; init; }
}
