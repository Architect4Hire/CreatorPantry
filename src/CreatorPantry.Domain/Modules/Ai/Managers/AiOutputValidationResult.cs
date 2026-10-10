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

    // ---- 11A.24: the read-only style test drive ----

    /// <summary>One of the three samples is empty, or is shorter than a sample can be and still be one.</summary>
    /// <remarks>
    /// All three are required and none is repaired. The value of the screen is two comparable columns, and a
    /// column the server padded out would be the server's writing presented as the model's.
    /// </remarks>
    public const string StyleSampleMissing = "ai.output.style_sample_missing";

    /// <summary>A sample is longer than its own limit — a whole article where an opening was asked for.</summary>
    public const string StyleSampleLengthOutOfRange = "ai.output.style_sample_length_out_of_range";

    // There is deliberately no reason code for naming a person or inferring a trait. Both are detected by a
    // best-effort heuristic (AiBrandGuideClaimScanner), which warns rather than refuses, so a rejection code for
    // either would be dead and would imply an enforcement this capability does not have. The warnings carry
    // AiBrandGuideClaimScanner's own codes instead.

    /// <summary>IMG-001 answered with too few or too many concepts.</summary>
    public const string PhotographyConceptCountOutOfRange = "ai.output.photography_concept_count_out_of_range";

    /// <summary>One concept planned too few or too many shots.</summary>
    public const string PhotographyShotCountOutOfRange = "ai.output.photography_shot_count_out_of_range";

    /// <summary>A concept or one of its shots left a required field blank.</summary>
    public const string PhotographyFieldMissing = "ai.output.photography_field_missing";

    /// <summary>
    /// A concept's shots do not form a shoot: there is no hero frame, more than one, or two shots claim the
    /// same role.
    /// </summary>
    /// <remarks>
    /// Exactly one hero per concept is what makes a concept addressable by IMG-002 — it composes the hero's
    /// prompt first — and distinct roles are what stop three frames of the same shot being presented as a shot
    /// list.
    /// </remarks>
    public const string PhotographyShotRolesInvalid = "ai.output.photography_shot_roles_invalid";

    /// <summary>Two concepts share a label, so a creator cannot tell them apart in a list of three.</summary>
    public const string PhotographyDuplicateLabel = "ai.output.photography_duplicate_label";

    /// <summary>
    /// A concept wrote a numeral where the brief gave it no number to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Numerals are refused outright in a photography concept, with one exception for a crop ratio
    /// (<c>4:5</c>).</strong> The rule this enforces is "no invented recipe fact": nothing in an IMG-001
    /// request tells the model a quantity, a time, a temperature, a yield or a serving count, so any such
    /// figure in a concept is fabricated — and a photograph planned around "bake 25 minutes" has put a recipe
    /// fact into a creative brief the creator may then act on (ai.md, recipes.md).
    /// </para>
    /// <para>
    /// <strong>Refusing every numeral rather than detecting food units is deliberate.</strong> A unit
    /// allow-list has to arbitrate <c>35mm</c>, <c>f/2.8</c>, <c>1/125s</c> and <c>180C</c>, and a rule that
    /// has to tell a lens from an oven is a rule that will get one wrong. A concept describes a picture in
    /// words — "overhead", "three-quarter", "late morning light" — and camera specifications belong to the
    /// shot list a photographer writes from it, not to the look itself. The prompt says so, so this code is
    /// what a model that ignored it receives.
    /// </para>
    /// </remarks>
    public const string PhotographyNumeralNotPermitted = "ai.output.photography_numeral_not_permitted";

    /// <summary>
    /// A concept made a safety, dietary, nutrition or authenticity claim about the food.
    /// </summary>
    /// <remarks>
    /// A photography concept describes how a dish should look. "Gluten-free", "healthy", "authentic" and
    /// "guaranteed" are claims about what it is, which this capability has no basis for and which ai.md
    /// forbids presenting as fact. Styling never carries one.
    /// </remarks>
    public const string PhotographyClaimNotPermitted = "ai.output.photography_claim_not_permitted";

    /// <summary>
    /// A concept carried a link, an account handle, markup or a code fence.
    /// </summary>
    /// <remarks>
    /// None belongs in a shoot plan, and each is the shape an injected instruction or an exfiltration attempt
    /// takes when it reaches a field a creator will read and act on.
    /// </remarks>
    public const string PhotographyTextNotPermitted = "ai.output.photography_text_not_permitted";

    /// <summary>The composed image prompt is missing, too short, or past its length.</summary>
    /// <remarks>
    /// Refused rather than trimmed: a prompt cut at a character boundary is a prompt the creator never read
    /// and the model never wrote, and this is the text that will be sent to an image model.
    /// </remarks>
    public const string ImagePromptLengthOutOfRange = "ai.output.image_prompt_length_out_of_range";

    /// <summary>The negative-guidance list is too long, or one of its phrases is.</summary>
    public const string ImagePromptAvoidInvalid = "ai.output.image_prompt_avoid_invalid";

    /// <summary>
    /// The prompt made a safety, dietary, nutrition or authenticity claim about the food.
    /// </summary>
    /// <remarks>
    /// The same bar <see cref="PhotographyClaimNotPermitted"/> sets, and it matters more here: this text is
    /// what gets sent to a provider and saved to the creator's library, so a claim in it outlives the request
    /// that produced it.
    /// </remarks>
    public const string ImagePromptClaimNotPermitted = "ai.output.image_prompt_claim_not_permitted";

    /// <summary>
    /// The prompt carried a link, an email address, an account handle, markup or a code fence.
    /// </summary>
    public const string ImagePromptTextNotPermitted = "ai.output.image_prompt_text_not_permitted";

    /// <summary>
    /// The prompt named a rendering parameter in its prose.
    /// </summary>
    /// <remarks>
    /// The document has no field for one, so this catches the other route: "--ar 16:9", "seed 1234", "steps
    /// 30", "Midjourney v6". IMG-002 composes a description of a photograph; what renders it, and with what
    /// settings, is 12.7's and not something a creator should have to edit out of their saved prompt.
    /// </remarks>
    public const string ImagePromptRenderDirectiveNotPermitted =
        "ai.output.image_prompt_render_directive_not_permitted";

    /// <summary>
    /// The reference-image observations are missing, too few, too many, duplicated by aspect, undeclared, or
    /// one of them states no confidence.
    /// </summary>
    /// <remarks>
    /// One code for the whole set, because every case is the same fault from a creator's point of view: the
    /// reading of their photograph did not come back in a form that can be shown beside it. The reason text
    /// says which, and the payload never appears in it.
    /// </remarks>
    public const string ReferenceImageObservationsInvalid = "ai.output.reference_image_observations_invalid";

    /// <summary>
    /// The reading identified a person, or asserted who owns a brand or a photograph.
    /// </summary>
    /// <remarks>
    /// Two of the four things IMG-004's RESTRICTION forbids, and the two a regex can actually catch: an
    /// identity claim about somebody in frame, and an ownership or trademark claim about a logo, a product or
    /// the picture itself. A model that can see an image will volunteer both unprompted, and either one
    /// reaches the creator's library as a statement CreatorPantry made about a real person or a real company.
    /// The third — a food-safety verdict — is caught by the shared claim ban. The fourth, an ingredient
    /// inferred rather than seen, cannot be judged from the text alone and is the evaluation set's.
    /// </remarks>
    public const string ReferenceImageIdentityClaimNotPermitted =
        "ai.output.reference_image_identity_claim_not_permitted";

    // ---- Reading a dish name into catalogue facets ----

    /// <summary>The same facet of the dish name was read twice.</summary>
    /// <remarks>
    /// Refused rather than deduplicated: the stored rows are keyed by facet, and choosing which of two
    /// contradictory readings to keep is not a decision to make silently on a creator's behalf.
    /// </remarks>
    public const string DishFacetDuplicate = "ai.output.dish_facet_duplicate";

    /// <summary>
    /// A facet suggestion names a code without saying how sure it is, or states a confidence while naming no
    /// code.
    /// </summary>
    /// <remarks>
    /// The first is the failure this capability most needs to refuse: an unqualified code is a guess that
    /// arrives looking exactly like a reading, and the surface pre-fills a creator's control from a confident
    /// one. The second is the mirror image — "fairly sure, about nothing" — and tolerating it would let the
    /// band and the code disagree about whether a reading exists at all.
    /// </remarks>
    public const string DishFacetConfidenceMisplaced = "ai.output.dish_facet_confidence_misplaced";

    /// <summary>A facet suggestion did not say which words of the name led to it, or why none did.</summary>
    /// <remarks>
    /// Required whether or not a code was named. It is what makes a suggestion checkable by the person who
    /// typed the name — a bare selection is not — and a declined facet with no reason tells them nothing.
    /// </remarks>
    public const string DishFacetUnexplained = "ai.output.dish_facet_unexplained";

    /// <summary>
    /// A facet rationale or warning made a claim about the food — a diet it suits, an allergen it lacks, or
    /// whether it is safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document has no <em>field</em> for any of that, but it has two free-text ones, and a rationale is
    /// rendered beside the control it explains — so "fattoush, so naturally gluten free" would reach a
    /// creator as a finding about their dish. This capability reads a <em>name</em>: the thinnest evidence in
    /// the product, and nowhere near enough for a dietary or safety conclusion (<c>ai.md</c>).
    /// </para>
    /// <para>
    /// Refused rather than warned, matching <see cref="ImagePromptClaimNotPermitted"/> and
    /// <see cref="PhotographyClaimNotPermitted"/> and against the same term list. A rationale has exactly one
    /// job — naming the words that led to a facet — so unlike generated prose there is no legitimate reading
    /// in which a claim belongs there and should merely be flagged.
    /// </para>
    /// </remarks>
    public const string DishFacetClaimNotPermitted = "ai.output.dish_facet_claim_not_permitted";

    /// <summary>
    /// A post was written for a channel the request did not name (AF.6.3).
    /// </summary>
    /// <remarks>
    /// Refused whole rather than dropped. Dropping it would be repairing the answer, and a model that writes
    /// for a channel nobody asked for has shown it was not following the list it was given.
    /// </remarks>
    public const string ChannelPostsChannelNotRequested = "ai.output.channel_posts_channel_not_requested";

    /// <summary>A requested channel has no post. Each requested channel is written exactly once.</summary>
    public const string ChannelPostsChannelMissing = "ai.output.channel_posts_channel_missing";

    /// <summary>One channel was written more than once, so nothing says which body is the answer.</summary>
    public const string ChannelPostsChannelRepeated = "ai.output.channel_posts_channel_repeated";

    /// <summary>
    /// A post body is blank, longer than one stored row can hold, or carries HTML or a code fence.
    /// </summary>
    /// <remarks>
    /// Not the channel's own limit: a body over that is accepted and flagged, never refused. This is the
    /// ceiling past which the body could not be stored at all.
    /// </remarks>
    public const string ChannelPostsBodyInvalid = "ai.output.channel_posts_body_invalid";

    /// <summary>A warning is blank, too long, undeclared, one too many, or about a channel nobody requested.</summary>
    public const string ChannelPostsWarningInvalid = "ai.output.channel_posts_warning_invalid";
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
