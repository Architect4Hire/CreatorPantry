namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Stable reason codes for a rejected model answer. Narrower than
/// <see cref="AiFailureCategory"/>, which is what gets recorded; these are what gets routed on.
/// </summary>
/// <remarks>
/// Every code is a string constant rather than an enum member so it can be logged, compared and asserted
/// without a mapping table, exactly as the application's other stable error codes are.
/// </remarks>
public static class AiOutputReason
{
    public const string EmptyPayload = "ai.output.empty_payload";
    public const string PayloadTooLarge = "ai.output.payload_too_large";
    public const string MalformedJson = "ai.output.malformed_json";
    public const string SchemaVersionMissing = "ai.output.schema_version_missing";
    public const string SchemaVersionMismatch = "ai.output.schema_version_mismatch";
    public const string UnknownField = "ai.output.unknown_field";
    public const string ShapeInvalid = "ai.output.shape_invalid";
    public const string ValueTooLong = "ai.output.value_too_long";
    public const string IllegalCharacters = "ai.output.illegal_characters";
    public const string KindNotDeclared = "ai.output.kind_not_declared";
    public const string FieldNameMisplaced = "ai.output.field_name_misplaced";
    public const string PositionMisplaced = "ai.output.position_misplaced";
    public const string ChangeSaysNothing = "ai.output.change_says_nothing";
    public const string ConflictingChanges = "ai.output.conflicting_changes";
    public const string OutsideScope = "ai.output.outside_scope";
    public const string WarningIndexInvalid = "ai.output.warning_index_invalid";

    /// <summary>The change addresses a row the pinned version does not contain.</summary>
    public const string TargetNotInSource = "ai.output.target_not_in_source";

    /// <summary>The proposed value cannot live in the field it targets.</summary>
    public const string DomainInvalid = "ai.output.domain_invalid";

    /// <summary>The recipe has moved past the version this proposal was computed against.</summary>
    public const string SourceChanged = "ai.output.source_changed";

    /// <summary>
    /// The change is well formed and addresses a real row, but no accepted change of that kind on that target
    /// has a path through ordinary recipe validation. See <see cref="AiChangeApplicability"/>.
    /// </summary>
    public const string NotApplicable = "ai.output.not_applicable";

    /// <summary>A concept-generation answer proposed fewer or more concepts than the capability requires.</summary>
    public const string ConceptCountOutOfRange = "ai.output.concept_count_out_of_range";

    /// <summary>Two proposed concepts share a title, so they are not the distinct concepts the task asked for.</summary>
    public const string DuplicateConceptTitle = "ai.output.duplicate_concept_title";

    /// <summary>A concept is missing its title, summary, or distinctness rationale.</summary>
    public const string ConceptFieldMissing = "ai.output.concept_field_missing";

    /// <summary>A recipe draft is missing a required field, or a required list has no usable entries.</summary>
    public const string RecipeDraftFieldMissing = "ai.output.recipe_draft_field_missing";

    /// <summary>A recipe draft proposed fewer or more ingredient or instruction groups than the capability allows.</summary>
    public const string RecipeDraftGroupCountOutOfRange = "ai.output.recipe_draft_group_count_out_of_range";

    /// <summary>An ingredient or instruction group was proposed with no lines or steps at all.</summary>
    public const string RecipeDraftEmptyGroup = "ai.output.recipe_draft_empty_group";

    /// <summary>A recipe draft proposed more lines, steps, or equipment items than one group or the draft allows.</summary>
    public const string RecipeDraftTooManyItems = "ai.output.recipe_draft_too_many_items";

    // ---- AIREC-004: ingredient substitution advice ----

    /// <summary>The proposed ranks are not exactly 1..N, so the alternatives are not ranked.</summary>
    public const string SubstitutionRankInvalid = "ai.output.substitution_rank_invalid";

    /// <summary>A substitution is missing its alternative, functional role, quantity guidance, or test recommendation.</summary>
    public const string SubstitutionFieldMissing = "ai.output.substitution_field_missing";

    /// <summary>Two proposed substitutions name the same alternative, so they are not distinct options.</summary>
    public const string DuplicateSubstitution = "ai.output.duplicate_substitution";

    /// <summary>
    /// A substitution claims high confidence on evidence it also declares unknown.
    /// </summary>
    /// <remarks>
    /// AIREC-004's own restriction, made checkable: unknown evidence stays unknown. A model that cannot say
    /// where a claim comes from may still offer it — as a low-confidence option a creator tests — but it may
    /// not present it as settled.
    /// </remarks>
    public const string ConfidenceUnevidenced = "ai.output.confidence_unevidenced";

    /// <summary>
    /// A substitution states an allergen consequence with no safety caution beside it.
    /// </summary>
    /// <remarks>
    /// The line between culinary plausibility and allergen safety, enforced rather than requested. An allergen
    /// statement presented as an ordinary impact note reads as a verdict; the caution is what keeps it a
    /// consequence the creator has to check.
    /// </remarks>
    public const string AllergenEffectUncautioned = "ai.output.allergen_effect_uncautioned";

    /// <summary>
    /// The answer proposed no substitution at all and did not say why.
    /// </summary>
    /// <remarks>
    /// "Nothing here I would stand behind" is a real and valuable answer — inventing an alternative to have
    /// something to show is the failure this prevents. An unexplained empty answer is not that; it is
    /// indistinguishable from a call that went wrong, and leaves the creator nothing to act on.
    /// </remarks>
    public const string SubstitutionAnswerUnexplained = "ai.output.substitution_answer_unexplained";

    // ---- AIREC-005: single-goal recipe adaptation ----

    /// <summary>
    /// A yield-goal answer set an ingredient quantity that disagrees with the deterministic scaling already
    /// computed for it.
    /// </summary>
    /// <remarks>
    /// Not correctable: the deterministic figure was already handed to the model as authoritative context, so
    /// a re-ask spends an attempt asking for the same arithmetic again rather than for something a retry could
    /// plausibly fix. <c>ai.md</c> routes scaling through deterministic domain code; this is what refuses the
    /// answer when a model did the arithmetic itself instead of copying the figure it was given.
    /// </remarks>
    public const string YieldMathNotDeterministic = "ai.output.yield_math_not_deterministic";

    /// <summary>
    /// An adaptation answer proposed no change toward the declared goal and did not say why.
    /// </summary>
    /// <remarks>
    /// The AIREC-005 form of <see cref="SubstitutionAnswerUnexplained"/>: "this goal cannot be met, or cannot
    /// be met safely" is a complete and honest answer, and inventing a change to have something to show is the
    /// fabricated success the capability's RESTRICTION forbids. What is required is specifically a
    /// <see cref="AiWarningKind.Limitation"/> addressed to the answer as a whole, not any warning at all — a
    /// stray assumption or caution should not by itself count as having explained an empty answer.
    /// </remarks>
    public const string AdaptationAnswerUnexplained = "ai.output.adaptation_answer_unexplained";

    // ---- AIREC-006: recipe quality and safety review ----

    /// <summary>A finding is missing its summary, or its field reference names no field kind.</summary>
    public const string FindingFieldMissing = "ai.output.finding_field_missing";

    /// <summary>
    /// A finding's field reference names an entity id where its field kind has no child to name, or names none
    /// where its field kind requires one.
    /// </summary>
    /// <remarks>
    /// Shape only — whether the id actually belongs to a line, step or equipment item in the pinned version is
    /// checked afterward, against the snapshot, where <see cref="AiOutputReason.TargetNotInSource"/> already
    /// covers a diff's target row for the same reason.
    /// </remarks>
    public const string FindingFieldRefInvalid = "ai.output.finding_field_ref_invalid";

    /// <summary>An editorial section, or an item in one, has no text; or a substitution or FAQ item lacks a field.</summary>
    public const string EditorialFieldMissing = "ai.output.editorial_field_missing";

    /// <summary>The same tip, question or ingredient line appears twice in one section.</summary>
    public const string EditorialDuplicateItem = "ai.output.editorial_duplicate_item";

    /// <summary>The answer carries a section the request did not ask for. Checked against the request, after the shape.</summary>
    public const string EditorialSectionNotRequested = "ai.output.editorial_section_not_requested";

    /// <summary>A substitution names an ingredient line that is not in the version the package was written against.</summary>
    public const string EditorialLineNotInSource = "ai.output.editorial_line_not_in_source";

    /// <summary>An SEO title or meta description is outside the configured length limits.</summary>
    public const string SeoLengthOutOfRange = "ai.output.seo_length_out_of_range";

    /// <summary>A key phrase breaks the configured format rules, or the list breaks its count rules.</summary>
    public const string SeoKeyPhraseInvalid = "ai.output.seo_key_phrase_invalid";

    /// <summary>Alt text breaks an accessibility rule: empty, over-long, a redundant "image of" prefix, or a repeat.</summary>
    public const string SeoAltTextInvalid = "ai.output.seo_alt_text_invalid";

    /// <summary>An internal-link idea is malformed: no anchor, no reason, a repeat, or a link to nothing.</summary>
    public const string SeoLinkInvalid = "ai.output.seo_link_invalid";

    /// <summary>An alt text or link names an asset or recipe that was not among those the request supplied.</summary>
    public const string SeoItemNotInSource = "ai.output.seo_item_not_in_source";

    /// <summary>
    /// A finding named an allergen or a diet where its category does not call for one, or omitted one where
    /// its category does.
    /// </summary>
    /// <remarks>
    /// The structural half of AIREC-006's own version of AIREC-004's "no guaranteed removal or suitability"
    /// rule: <c>AllergenConflict</c> requires <c>Allergen</c> and <c>AllergenEffect</c>, <c>DietaryConflict</c>
    /// requires <c>Diet</c> and <c>DietaryEffect</c>, and every other category requires none of the four. A
    /// finding that mismatches this has not named its consequence in the one place the schema can check it.
    /// </remarks>
    public const string SafetyEffectMisplaced = "ai.output.safety_effect_misplaced";

    /// <summary>
    /// A finding categorised <c>UnsupportedClaim</c> did not say what is unknown.
    /// </summary>
    /// <remarks>
    /// AIREC-006's own restriction, made checkable: flagging a claim as unsupported is not itself the answer —
    /// the RESTRICTION requires saying what is unknown, and a finding that names the category without naming
    /// the gap has not done that.
    /// </remarks>
    public const string UnsupportedClaimUnexplained = "ai.output.unsupported_claim_unexplained";

    /// <summary>
    /// A finding that needs a reference check by AIREC-006's own floor — every allergen or dietary conflict,
    /// and every major or critical finding of any category — declared that it does not.
    /// </summary>
    public const string ReferenceCheckRequired = "ai.output.reference_check_required";

    /// <summary>
    /// A finding whose <c>RequiresReferenceCheck</c> is <c>true</c> carries no safety caution beside it.
    /// </summary>
    /// <remarks>
    /// Not limited to <c>AllergenConflict</c> or <c>DietaryConflict</c>: <see cref="AiWarningKind.SafetyCaution"/>
    /// is documented to cover "preservation, temperature, allergens, or dietary suitability" together, so a
    /// critical finding about an undercooked step or an unsafe preservation method owes the same caution an
    /// allergen conflict does — the reference-check flag and the caution travel together, whatever the
    /// category.
    /// </remarks>
    public const string SafetyFindingUncautioned = "ai.output.safety_finding_uncautioned";

    /// <summary>An allergen or dietary conflict was reported below the severity floor that category requires.</summary>
    public const string SafetyFindingSeverityTooLow = "ai.output.safety_finding_severity_too_low";

    /// <summary>
    /// The answer proposed no finding at all and did not say why.
    /// </summary>
    /// <remarks>
    /// The AIREC-006 form of <see cref="SubstitutionAnswerUnexplained"/>: "this recipe reads as complete and
    /// consistent" is a real and useful answer, and inventing a finding to have something to show is the
    /// fabricated result the capability's RESTRICTION forbids. What is required is a warning addressed to the
    /// answer as a whole, not any finding.
    /// </remarks>
    public const string ReviewAnswerUnexplained = "ai.output.review_answer_unexplained";

    /// <summary>
    /// A section or rule claims the sources support it but cites none, or cites a passage the request never
    /// offered.
    /// </summary>
    /// <remarks>
    /// The structural half of 11A.17's "source citations" requirement. A citation is checked against the exact
    /// passage rows the handler read for this request, so a model cannot invent one and a passage belonging to
    /// another workspace was never a candidate. An answer that merely claims to be sourced is refused rather
    /// than relabelled.
    /// </remarks>
    public const string BrandGuideCitationInvalid = "ai.output.brand_guide_citation_invalid";

    /// <summary>A conflict names fewer than two passages, so it reports a disagreement with only one side.</summary>
    /// <remarks>
    /// "Do not hide contradictory evidence" means the contradiction has to be checkable. One citation is an
    /// opinion about a passage; two are a finding the creator can go and read.
    /// </remarks>
    public const string BrandGuideConflictUnsupported = "ai.output.brand_guide_conflict_unsupported";

    /// <summary>
    /// A section names a channel key the request did not offer, or carries one on a dimension that takes none.
    /// </summary>
    public const string BrandGuideChannelInvalid = "ai.output.brand_guide_channel_invalid";

    /// <summary>The answer wrote a dimension the request did not ask for, or wrote one twice.</summary>
    public const string BrandGuideDimensionInvalid = "ai.output.brand_guide_dimension_invalid";

    /// <summary>
    /// A body or rule reproduces a long run of words from a cited passage.
    /// </summary>
    /// <remarks>
    /// 11A.17's RESTRICTION lists copying long source passages beside mixing workspaces, so it is a refusal and
    /// not a caution. Guidance describes how the creator writes; it does not reproduce what they wrote, and
    /// nothing is truncated into something the model never said.
    /// </remarks>
    public const string BrandGuidePassageCopied = "ai.output.brand_guide_passage_copied";

    // There is deliberately no reason code for naming a person or inferring a trait. Both are detected by a
    // best-effort heuristic (AiBrandGuideClaimScanner), which warns rather than refuses, so a rejection code for
    // either would be dead and would imply an enforcement this capability does not have. The warnings carry
    // AiBrandGuideClaimScanner's own codes instead.
}

/// <summary>Why one model answer was rejected, in terms safe to store and to route on.</summary>
/// <param name="Category">What gets recorded on the operation and the execution row.</param>
/// <param name="ReasonCode">One of <see cref="AiOutputReason"/>.</param>
/// <param name="Message">
/// A sanitized explanation, bounded to <see cref="AiPolicy.DiagnosticMaxLength"/>. Never contains the
/// payload, a fragment of it, or any value from it.
/// </param>
/// <param name="IsCorrectableByReprompt">
/// Whether asking the provider again, with the failure described, could plausibly produce a valid answer. The
/// validator reports it; the execution wrapper decides whether to spend an attempt on it, and how many.
/// </param>
public sealed record AiOutputFailure(
    AiFailureCategory Category,
    string ReasonCode,
    string Message,
    bool IsCorrectableByReprompt);

/// <summary>A validated model answer, or the reason there isn't one.</summary>
/// <remarks>
/// The raw payload does not appear on either path. A caller that receives a failure gets a reason code and a
/// sanitized message and cannot reach the JSON through this type at all — which is what "raw model JSON never
/// reaches Business or persistence" has to mean in practice.
/// </remarks>
public sealed class AiOutputValidationResult
{
    private AiOutputValidationResult(AiOutputDocument? document, AiOutputFailure? failure) =>
        (Document, Failure) = (document, failure);

    public AiOutputDocument? Document { get; }

    public AiOutputFailure? Failure { get; }

    public bool Succeeded => Failure is null;

    public static AiOutputValidationResult Success(AiOutputDocument document) => new(document, null);

    public static AiOutputValidationResult Failed(AiOutputFailure failure) => new(null, failure);
}
