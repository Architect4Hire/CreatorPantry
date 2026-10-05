using CreatorPantry.Domain.Managers.Idempotency;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Limits and invariants for the AI operation aggregate, shared by EF configuration and by the validation
/// that arrives with the write seam.
/// </summary>
public static class AiPolicy
{
    /// <summary>
    /// The idempotency key a caller supplies to make requesting an operation replay-safe.
    /// </summary>
    /// <remarks>
    /// The same limit the generic idempotency record uses, and taken from it rather than restated, so the two
    /// cannot drift into a state where a key is storable in one place and not the other. The mechanisms stay
    /// separate on purpose: the generic record makes an HTTP command replay-safe and expires, while this
    /// column is a permanent property of the operation row and is what stops one request producing two
    /// operations after the record has aged out.
    /// </remarks>
    public const int IdempotencyKeyMaxLength = IdempotencyPolicy.KeyMaxLength;

    /// <summary>A stable identifier a provider, model, or deployment is known by.</summary>
    public const int ProviderIdentifierMaxLength = 200;

    /// <summary>A prompt template's id, matching what the template store accepts.</summary>
    public const int TemplateIdMaxLength = 200;

    /// <summary>A template's <c>major.minor.patch</c> version, and its <c>sha256:</c> body checksum.</summary>
    public const int TemplateVersionMaxLength = 32;

    /// <inheritdoc cref="TemplateVersionMaxLength"/>
    public const int ChecksumMaxLength = 80;

    /// <summary>The output-schema version a proposal was validated against.</summary>
    public const int SchemaVersionMaxLength = 100;

    /// <summary>The field a <see cref="AiChangeKind.Set"/> targets, e.g. <c>headnote</c>.</summary>
    /// <remarks>
    /// Bounded here; which names are <em>allowed</em> for a given target kind belongs to the structured-output
    /// validator, which is the layer that knows the schema a proposal was held to.
    /// </remarks>
    public const int FieldNameMaxLength = 100;

    /// <summary>
    /// A proposed or superseded value. As generous as the recipe text it has to be able to carry — an
    /// instruction step is the longest thing a change can hold, and truncating a proposal would make the
    /// creator review something the model did not say.
    /// </summary>
    public const int ChangeValueMaxLength = 4000;

    /// <summary>A warning's message, and a creator's feedback. Prose, not content.</summary>
    public const int MessageMaxLength = 1000;

    /// <summary>A recipe concept's title, summary, or distinctness rationale.</summary>
    public const int ConceptFieldMaxLength = 1000;

    /// <summary>One assumption, suggested ingredient, or dietary note listed on a concept.</summary>
    public const int ConceptListItemMaxLength = 300;

    /// <summary>
    /// The most assumptions, suggested ingredients, or dietary notes one concept may list.
    /// </summary>
    /// <remarks>
    /// Bounded so the joined string <see cref="RecipeConceptsAiTaskHandler"/> stores in one
    /// <c>AiStructuredChange.AfterValue</c> column cannot exceed <see cref="ChangeValueMaxLength"/> — ten items
    /// at <see cref="ConceptListItemMaxLength"/> each is comfortably inside it, so a proposal is refused here
    /// rather than reaching SQL Server as a truncation error.
    /// </remarks>
    public const int ConceptListMaxItems = 10;

    /// <summary>
    /// The fewest recipe concepts a concept-generation answer may propose.
    /// </summary>
    /// <remarks>"Return multiple distinct concepts" (AIREC-001) is a floor the answer is held to, not a hope.</remarks>
    public const int MinConceptCount = 2;

    /// <summary>The most recipe concepts one answer may propose, so a review panel stays reviewable.</summary>
    public const int MaxConceptCount = 5;

    /// <summary>
    /// One of AIREC-001's short brief fields: audience, course, cuisine, skill, season, or time budget.
    /// </summary>
    public const int BriefFieldMaxLength = 200;

    /// <summary>
    /// One of AIREC-001's comma-separated brief fields: dietary goals, available ingredients, exclusions,
    /// equipment, or creator style. Longer than <see cref="BriefFieldMaxLength"/> because several distinct
    /// items are expected to share the one field.
    /// </summary>
    public const int BriefListFieldMaxLength = 500;

    /// <summary>
    /// The bound on an operation's stored <c>TaskInputsJson</c>: every declared brief field, each individually
    /// bounded above, plus a selected concept, with room for JSON structure and key names.
    /// </summary>
    /// <remarks>
    /// <strong>Enforced in code rather than by the column.</strong> AIREC-002's request may carry the eleven
    /// brief fields <em>and</em> a selected concept's title and summary, which together exceed 4000 characters
    /// at their declared bounds — and SQL Server has no <c>nvarchar(8000)</c>, so any increase past 4000 means
    /// <c>nvarchar(max)</c>. The column is therefore unbounded and this constant is what a request is actually
    /// held to, checked by <c>AiFirstDraftRequestBusiness</c> before the row is written. Keeping the number
    /// here means the limit is still one documented value rather than whatever the storage type permits.
    /// </remarks>
    public const int TaskInputsJsonMaxLength = 12000;

    /// <summary>
    /// What a creator asks a revision to achieve, in their own words.
    /// </summary>
    /// <remarks>
    /// A sentence or two, not a brief. A goal long enough to be a specification is one the creator should be
    /// scoping into several revisions they can review separately — and the field is untrusted prompt content,
    /// so a generous limit here is an invitation rather than a convenience.
    /// </remarks>
    public const int RevisionGoalMaxLength = 500;

    /// <summary>
    /// The composed <c>selectedConcept</c> line AIREC-002 carries: one concept's title and summary, joined.
    /// </summary>
    /// <remarks>
    /// Title and summary only. A concept's <c>distinctnessRationale</c> is about telling it apart from the
    /// siblings it was proposed beside, which means nothing to a draft generated from it alone, and its
    /// <c>suggestedIngredients</c> are a pitch rather than a list — carrying them would read to the model as a
    /// constraint the creator never stated. Both are still on the concept for a creator to read.
    /// </remarks>
    public const int SelectedConceptMaxLength = (ConceptFieldMaxLength * 2) + 8;

    // ---- AIREC-002: structured first-draft output (AiRecipeDraftOutputDocument) ----
    //
    // Sized independently of RecipePolicy's own limits, not by referencing them — AiPolicy and RecipePolicy
    // are different modules' Managers types, and backend.md's module-boundary rule treats a Managers type the
    // same as any other domain model that must not cross a module boundary directly. The values below are
    // chosen to match RecipePolicy's limits in spirit, so an accepted draft that cleared this validator can
    // never then be refused by RecipePolicy's own bounds at 9.4b's acceptance step.

    /// <summary>A recipe draft's proposed title.</summary>
    public const int RecipeDraftTitleMaxLength = 200;

    /// <summary>A recipe draft's proposed description.</summary>
    public const int RecipeDraftDescriptionMaxLength = 2000;

    /// <summary>A recipe draft's proposed working notes.</summary>
    public const int RecipeDraftNotesMaxLength = 4000;

    /// <summary>The proposed yield exactly as phrased: "makes 12 muffins".</summary>
    public const int RecipeDraftYieldTextMaxLength = 200;

    /// <summary>
    /// The yield unit as free text ("loaves", "cups") — never a <c>MeasurementUnitId</c>; see the module-level
    /// remark on why no vocabulary identifier appears anywhere in this document.
    /// </summary>
    public const int RecipeDraftYieldUnitTextMaxLength = 64;

    /// <summary>An ingredient-group or instruction-group heading: "For the streusel".</summary>
    public const int RecipeDraftGroupTitleMaxLength = 200;

    /// <summary>One ingredient line or equipment line, exactly as proposed.</summary>
    public const int RecipeDraftLineTextMaxLength = 500;

    /// <summary>One instruction step's text.</summary>
    public const int RecipeDraftStepTextMaxLength = 4000;

    /// <summary>A per-line preparation note, a per-step aside, or an equipment note.</summary>
    public const int RecipeDraftNoteMaxLength = 1000;

    /// <summary>The parsed ingredient-name span of a line.</summary>
    public const int RecipeDraftIngredientNameTextMaxLength = 128;

    /// <summary>The unit span of an ingredient line as proposed: "cups", "tablespoons", "large".</summary>
    public const int RecipeDraftUnitTextMaxLength = 64;

    /// <summary>One open question the model could not confidently resolve on its own.</summary>
    public const int RecipeDraftUnresolvedQuestionMaxLength = 500;

    /// <summary>The fewest ingredient groups a draft may propose. Every recipe has at least one ingredient list.</summary>
    public const int MinIngredientGroups = 1;

    /// <summary>The most ingredient groups one draft may propose, so a review stays reviewable.</summary>
    public const int MaxIngredientGroups = 10;

    /// <summary>The most ingredient lines one group may propose.</summary>
    public const int MaxIngredientLinesPerGroup = 30;

    /// <summary>The fewest instruction groups a draft may propose. Every recipe has at least one method.</summary>
    public const int MinInstructionGroups = 1;

    /// <summary>The most instruction groups one draft may propose.</summary>
    public const int MaxInstructionGroups = 10;

    /// <summary>The most instruction steps one group may propose.</summary>
    public const int MaxInstructionStepsPerGroup = 30;

    /// <summary>The most equipment items one draft may propose.</summary>
    public const int MaxEquipmentItems = 20;

    /// <summary>The most unresolved questions one draft may list.</summary>
    public const int MaxUnresolvedQuestions = 10;

    /// <summary>
    /// The most warnings one draft may list.
    /// </summary>
    /// <remarks>
    /// Without a cap, a whole-draft warning list carrying a genuinely important <c>SafetyCaution</c> could
    /// have that caution diluted among an unbounded number of low-value <c>Assumption</c> entries — the exact
    /// risk a review flagged. <see cref="AiConceptOutputValidator"/> has the same uncapped gap today; this
    /// document is the one most likely to carry a real safety caution, so it is closed here first.
    /// </remarks>
    public const int MaxRecipeDraftWarnings = 20;

    // ---- AIREC-004: ingredient substitution advice (AiSubstitutionOutputDocument) ----

    /// <summary>
    /// What the creator wants out of a substitution, in their own words: "I am out of buttermilk".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same bound as <see cref="RevisionGoalMaxLength"/>, and restated rather than aliased because the two
    /// fields are bounded for different reasons and one may move without the other. A goal is scoped work; a
    /// substitution reason is context.
    /// </para>
    /// <para>
    /// <strong>This is the field a creator types an allergy into</strong>, which is exactly why it travels as
    /// untrusted preference text and never as part of the task's instructions. A reason that names a person's
    /// allergy is not a permission to declare anything safe for them.
    /// </para>
    /// </remarks>
    public const int SubstitutionReasonMaxLength = 500;

    /// <summary>One substitution's alternative name, functional role, or any single impact note.</summary>
    public const int SubstitutionFieldMaxLength = 1000;

    /// <summary>One dietary note or allergen name listed on a substitution.</summary>
    public const int SubstitutionListItemMaxLength = 300;

    /// <summary>
    /// The most dietary notes or allergen effects one substitution may list.
    /// </summary>
    /// <remarks>
    /// Bounded for the reason <see cref="ConceptListMaxItems"/> is — the joined string a list becomes in one
    /// <c>AiStructuredChange.AfterValue</c> must stay inside <see cref="ChangeValueMaxLength"/> — and for the
    /// reason <see cref="MaxRecipeDraftWarnings"/> is: an allergen consequence that matters must not be
    /// diluted among a dozen that do not.
    /// </remarks>
    public const int SubstitutionListMaxItems = 10;

    /// <summary>
    /// The most alternatives one answer may propose.
    /// </summary>
    /// <remarks>
    /// There is no minimum, and that is deliberate. "No alternative I would stand behind" is a correct answer
    /// for an ingredient carrying the structure of a dish, and a floor here would make inventing one the only
    /// way to return successfully. <see cref="AiOutputReason.SubstitutionAnswerUnexplained"/> is what keeps an
    /// empty answer honest instead.
    /// </remarks>
    public const int MaxSubstitutionCount = 5;

    /// <summary>The most warnings one substitution answer may carry.</summary>
    /// <inheritdoc cref="MaxRecipeDraftWarnings" path="/remarks"/>
    public const int MaxSubstitutionWarnings = 20;

    // ---- AIREC-005: single-goal recipe adaptation (reuses AiOutputDocument; see AiAdaptationOutputValidator) ----

    /// <summary>
    /// What the declared goal means for this recipe, in the creator's own words: "gluten-free", "no stand
    /// mixer", "a nervous first-timer".
    /// </summary>
    /// <remarks>
    /// The same bound as <see cref="RevisionGoalMaxLength"/> and <see cref="SubstitutionReasonMaxLength"/>,
    /// restated rather than aliased for the reason those two are: each field is bounded for its own capability
    /// and may move independently. Untrusted prompt content throughout, exactly like the other two.
    /// </remarks>
    public const int AdaptationGoalDetailMaxLength = 500;

    /// <summary>
    /// The most warnings one adaptation answer may carry.
    /// </summary>
    /// <inheritdoc cref="MaxRecipeDraftWarnings" path="/remarks"/>
    public const int MaxAdaptationWarnings = 20;

    // ---- AIREC-006: recipe quality and safety review (AiRecipeReviewOutputDocument) ----

    /// <summary>One finding's summary, or its evidence note.</summary>
    public const int FindingFieldMaxLength = 1000;

    /// <summary>The allergen or diet name on an <c>AllergenConflict</c> or <c>DietaryConflict</c> finding.</summary>
    /// <remarks>
    /// The same bound as <see cref="SubstitutionListItemMaxLength"/>, restated rather than aliased for the
    /// reason <see cref="SubstitutionReasonMaxLength"/>'s own remark gives: each capability's field is bounded
    /// for its own reason and may move independently.
    /// </remarks>
    public const int FindingSafetyNameMaxLength = 300;

    /// <summary>One thing a finding names as unknown, when its category is <c>UnsupportedClaim</c>.</summary>
    public const int FindingUnknownFactorMaxLength = 300;

    /// <summary>
    /// The most unknown factors one finding may list.
    /// </summary>
    /// <inheritdoc cref="SubstitutionListMaxItems" path="/remarks"/>
    public const int MaxUnknownFactorsPerFinding = 10;

    /// <summary>
    /// The most findings one review may return.
    /// </summary>
    /// <remarks>
    /// There is no minimum. "This recipe reads as complete and consistent" is a correct and valuable answer for
    /// an ordinary recipe, and a floor here would make inventing a finding the only way to return successfully
    /// — exactly the fabricated-issue failure AIREC-006's RESTRICTION forbids. The cap exists for the reason
    /// <see cref="MaxSubstitutionCount"/>'s does: a review panel a creator can actually work through.
    /// </remarks>
    public const int MaxFindingCount = 30;

    /// <summary>The most warnings one review answer may carry.</summary>
    /// <inheritdoc cref="MaxRecipeDraftWarnings" path="/remarks"/>
    public const int MaxReviewWarnings = 20;

    // ---- RCPUB-001: editorial package (AiEditorialPackageOutputDocument) ----

    /// <summary>A headnote, introduction, storage note or call to action. Under <see cref="ChangeValueMaxLength"/>.</summary>
    public const int EditorialTextMaxLength = 2000;

    public const int EditorialItemMaxLength = 600;

    public const int MaxEditorialTips = 5;

    public const int MaxEditorialSubstitutions = 5;

    public const int MaxEditorialFaqItems = 6;

    public const int MaxEditorialWarnings = 20;

    /// <summary>
    /// Stored at the front of every warning a model wrote, so a reader can tell it from a server finding
    /// (<c>[code]</c>) by a positive label on each rather than by the absence of one. The validators refuse the
    /// label in model text and leave room for it under <see cref="MessageMaxLength"/>.
    /// </summary>
    public const string ModelWarningLabel = "[model] ";

    // ---- RCPUB-002: SEO package (AiSeoPackageOutputDocument). The length rules themselves are configuration: see SeoRules. ----

    public const int MaxSeoAltTexts = 20;

    public const int MaxSeoWarnings = 20;

    public const int SeoReasonMaxLength = 300;

    /// <summary>
    /// How many source documents one brand-guide proposal request may name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This module's own cap, deliberately duplicating the brand module's.</strong> The brand module caps
    /// what its grounding read will <em>supply</em>; this caps what a request may <em>ask for</em>, and it is
    /// part of this route's contract — a client is told the limit before it sends. Naming the brand module's
    /// constant here would reach across a module boundary for a value that belongs to a different decision, which
    /// <c>ModuleBoundaryTests</c> refuses, and rightly: the two could legitimately differ.
    /// </para>
    /// <para>
    /// If they do differ, nothing is lost silently. The grounding read reports every selection it could not
    /// supply, including ones it trimmed for its own budget, and the proposal carries that as a finding.
    /// </para>
    /// </remarks>
    public const int BrandGuideMaxSourceDocuments = 10;

    /// <summary>The longest body one brand-guide dimension's guidance may be.</summary>
    /// <remarks>
    /// Inside <see cref="ChangeValueMaxLength"/>, because the handler stores a body as one
    /// <c>AiStructuredChange.AfterValue</c> and a proposal refused at persistence has already cost a provider
    /// call.
    /// </remarks>
    public const int BrandGuideBodyMaxLength = 2000;

    /// <summary>The longest one brand-guide do/don't rule may be.</summary>
    public const int BrandGuideRuleMaxLength = 500;

    /// <summary>The longest one conflict or uncertainty summary may be.</summary>
    public const int BrandGuideSummaryMaxLength = 500;

    /// <summary>How many do/don't rules one brand-guide proposal may offer.</summary>
    public const int BrandGuideMaxRules = 30;

    /// <summary>How many conflicts or uncertainties one brand-guide proposal may report.</summary>
    public const int BrandGuideMaxFindings = 20;

    /// <summary>How many passages one section, rule or conflict may cite.</summary>
    public const int BrandGuideMaxCitationsPerItem = 8;

    /// <summary>
    /// The longest run of consecutive words a brand-guide answer may share with a cited passage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The enforceable form of "do not copy long source passages". Measured in words rather than characters
    /// because that is the unit a reader recognises as a quotation, and compared case- and
    /// punctuation-insensitively so reformatting does not evade it.
    /// </para>
    /// <para>
    /// Twelve is long enough that ordinary overlap — a brand's own recurring phrase, a product name, a short
    /// idiom the guidance is explicitly about — passes, and short enough that a reproduced sentence does not.
    /// The creator's distinctive vocabulary is exactly what a guide is supposed to name, so a floor much lower
    /// than this would refuse correct answers.
    /// </para>
    /// </remarks>
    public const int BrandGuideMaxQuotedWordRun = 12;

    /// <summary>
    /// How few passages make a brand-guide proposal's evidence thin enough for the server to say so.
    /// </summary>
    /// <remarks>
    /// A floor on the grounding, not on the answer. Below it the handler attaches a
    /// <see cref="AiWarningKind.Limitation"/> warning whatever the model returned, because "do not hide thin
    /// evidence" cannot be left to the thing whose answer looks better without it.
    /// </remarks>
    public const int BrandGuideSparseEvidenceFloor = 4;

    /// <summary>
    /// The ceiling, in estimated tokens, on one assembled <see cref="BrandContextPackage"/> (11A.19).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bound on how much brand material one generation carries, which is what "bounded" means in that
    /// prompt's own words. Three thousand is roughly a page and a half of guidance plus a dozen short excerpts:
    /// enough for a filled-in guide with its rules and evidence, and far short of a context window, so the task
    /// inputs and the recipe a generation is actually about still have room.
    /// </para>
    /// <para>
    /// Measured with <see cref="BrandContextSelection.EstimateTokens"/>, which is an estimate rather than a
    /// tokenizer's count — so this is a budget, not a guarantee about any provider's accounting. It is deliberately
    /// generous for that reason: a limit enforced against an approximation should fail on content that is
    /// obviously too long, not on content near the line.
    /// </para>
    /// </remarks>
    public const int BrandContextMaxEstimatedTokens = 3_000;

    /// <summary>
    /// How many source documents one brand-context assembly may draw excerpts from.
    /// </summary>
    /// <remarks>
    /// Matches <see cref="BrandGuideMaxSourceDocuments"/> in value and not by reference, for the reason that
    /// constant's own remarks give about the brand module's grounding cap: two separate decisions that happen to
    /// agree. A request naming more is refused rather than trimmed, because a caller that asked for twelve
    /// documents and silently got ten would believe it had read all twelve.
    /// </remarks>
    public const int BrandContextMaxSourceDocuments = 10;

    /// <summary>
    /// How many documents the assembler picks for itself when a request names none.
    /// </summary>
    /// <remarks>
    /// Lower than <see cref="BrandContextMaxSourceDocuments"/> on purpose. A caller naming documents has made a
    /// choice and gets the full allowance; relevance matching is a guess the server is making on the creator's
    /// behalf, and a guess should spend less of the budget than an instruction.
    /// </remarks>
    public const int BrandContextMaxSelectedSourceDocuments = 4;

    /// <summary>
    /// The longest channel key a recorded brand context may carry.
    /// </summary>
    /// <remarks>
    /// This module's own number, deliberately not the brand module's <c>ChannelKeyMaxLength</c>: a policy never
    /// crosses a module boundary and <c>ModuleBoundaryTests</c> is what says so. The two agree today because a
    /// key that fits one side has to fit the other; the day they disagree, a provenance row quietly truncating a
    /// key it was handed is the failure to avoid, so the column refuses it instead.
    /// </remarks>
    public const int BrandContextChannelKeyMaxLength = 64;

    /// <summary>
    /// The longest audience a recorded brand context may carry. See <see cref="BrandContextChannelKeyMaxLength"/>
    /// for why this module states its own length rather than reading the brand module's.
    /// </summary>
    public const int BrandContextAudienceMaxLength = 500;

    // ---- 11A.24: the read-only style test drive ----

    /// <summary>
    /// The longest subject a creator may name for their own test drive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Short on purpose, and the shortest free-text field any capability accepts. The subject exists so a
    /// creator sees the comparison on their own food rather than on
    /// <c>BrandStyleTestDriveSubject.Default</c>; it is not a brief, and a field long enough to hold
    /// instructions would be a way to steer a generation the request has no other way to steer.
    /// </para>
    /// <para>
    /// It is also the <em>only</em> thing that differs between the two calls' task material — both halves
    /// receive the same subject, because a comparison in which the subject moved would be measuring the
    /// subject.
    /// </para>
    /// </remarks>
    public const int StyleSampleSubjectMaxLength = 120;

    /// <summary>The longest a sample blog introduction may be.</summary>
    /// <remarks>
    /// A sample, not a post. Each of the three limits below is roughly what the piece runs to in practice, so a
    /// model that writes a whole article instead of an opening is refused rather than shown in a panel it does
    /// not fit.
    /// </remarks>
    public const int StyleSampleBlogIntroMaxLength = 900;

    /// <inheritdoc cref="StyleSampleBlogIntroMaxLength"/>
    public const int StyleSampleSocialCaptionMaxLength = 400;

    /// <inheritdoc cref="StyleSampleBlogIntroMaxLength"/>
    public const int StyleSampleImagePromptMaxLength = 600;

    /// <summary>
    /// The shortest a sample may be and still be one.
    /// </summary>
    /// <remarks>
    /// A floor rather than a repair: four words is not a blog introduction, and the whole value of the screen
    /// is that the two columns are comparable. An answer below this is refused, because padding it would make
    /// the server the author of something the creator is being shown as the model's work.
    /// </remarks>
    public const int StyleSampleMinLength = 40;

    /// <summary>How many warnings one half of a test drive may carry.</summary>
    /// <remarks>
    /// Far below the twenty every other capability allows, because there are three short samples here rather
    /// than a package of sections — and because both halves' warnings land on one proposal, so the ceiling is
    /// effectively twice this.
    /// </remarks>
    public const int MaxStyleSampleWarnings = 6;

    /// <summary>How many of a workspace's own recipes are offered to the model as internal-link candidates.</summary>
    public const int MaxSeoLinkCandidates = 40;

    /// <summary>
    /// The one free-text field a provider's own words may land in: a sanitized failure summary.
    /// </summary>
    /// <remarks>
    /// Short deliberately. It is a diagnostic, and ai.md forbids logging prompt bodies or generated creator
    /// content — a generous limit here would invite someone to paste a whole provider payload into it.
    /// </remarks>
    public const int DiagnosticMaxLength = 500;

    /// <summary>
    /// The largest model answer the validator will even parse.
    /// </summary>
    /// <remarks>
    /// Checked before parsing, because parsing is where an oversized payload costs something. A proposal is a
    /// list of field-sized changes, so this is generous by a wide margin — it is a backstop against a provider
    /// returning something pathological, not a limit any real answer should approach.
    /// </remarks>
    public const int OutputPayloadMaxBytes = 256 * 1024;

    /// <summary>
    /// Which parts of a recipe each scope permits a change to touch.
    /// </summary>
    /// <remarks>
    /// The table that makes <see cref="AiOperationScope"/> enforceable rather than decorative. A change
    /// addressing anything outside its operation's scope is rejected, not trimmed: the creator asked for a
    /// bounded change, and quietly widening it is the failure the column exists to prevent.
    /// </remarks>
    public static IReadOnlySet<AiChangeTargetKind> AllowedTargets(AiOperationScope scope) => scope switch
    {
        AiOperationScope.WholeRecipe => AllTargets,
        AiOperationScope.Ingredients => IngredientTargets,
        AiOperationScope.Instructions => InstructionTargets,
        AiOperationScope.Metadata => MetadataTargets,
        AiOperationScope.Media => MediaTargets,
        _ => NoTargets,
    };

    /// <summary>
    /// Which fields each scope permits on one target kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The target check is not enough, and <see cref="AiOperationScope.Metadata"/> is why.</strong>
    /// That scope describes itself as "the recipe's framing, not its method" — title, description, headnote,
    /// times, yield — but it permits the <see cref="AiChangeTargetKind.Recipe"/> target, and a <c>Set</c> on
    /// the recipe reaches <c>notes</c>, <c>storageNotes</c> and <c>attributionText</c> as well. A creator who
    /// scoped a revision to metadata and had their storage notes rewritten got exactly what
    /// <see cref="AiOperationScope"/> promises cannot happen.
    /// </para>
    /// <para>
    /// So a scope bounds fields as well as targets. A field outside its scope is rejected rather than
    /// dropped, for the reason the target rule gives: the creator asked for a bounded change, and quietly
    /// narrowing the answer is as wrong as quietly widening it — they would be reviewing a diff that is not
    /// the one the model proposed.
    /// </para>
    /// <para>
    /// <see cref="AiOperationScope.WholeRecipe"/> defers to <see cref="AiDiffFields"/> entirely: it is the
    /// scope that means "anything", so restating the field list here would be a second allow-list to keep in
    /// step with the first.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> AllowedFields(AiOperationScope scope, AiChangeTargetKind target)
    {
        if (!AllowedTargets(scope).Contains(target))
        {
            return NoFields;
        }

        return scope is AiOperationScope.Metadata && target is AiChangeTargetKind.Recipe
            ? MetadataRecipeFields
            : AiDiffFields.For(target).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The recipe's framing, as <see cref="AiOperationScope.Metadata"/> describes itself.
    /// </summary>
    /// <remarks>
    /// <c>notes</c>, <c>storageNotes</c> and <c>attributionText</c> are deliberately absent. Working notes and
    /// storage guidance are the creator's own record of how the dish behaves, and an attribution says where a
    /// recipe came from — none of the three is framing a revision of the title and times was asked to touch.
    /// They remain reachable under <see cref="AiOperationScope.WholeRecipe"/>, which is the scope that says so.
    /// </remarks>
    private static readonly IReadOnlySet<string> MetadataRecipeFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "description", "headnote",
        "prepTimeMinutes", "cookTimeMinutes", "restTimeMinutes", "totalTimeMinutes",
        "yieldText", "yieldQuantity", "servingCount", "servingSize",
    };

    private static readonly IReadOnlySet<string> NoFields = new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<AiChangeTargetKind> AllTargets =
        Enum.GetValues<AiChangeTargetKind>().Where(kind => kind is not AiChangeTargetKind.Unspecified).ToHashSet();

    private static readonly IReadOnlySet<AiChangeTargetKind> IngredientTargets =
        new HashSet<AiChangeTargetKind> { AiChangeTargetKind.Ingredient, AiChangeTargetKind.IngredientGroup };

    private static readonly IReadOnlySet<AiChangeTargetKind> InstructionTargets =
        new HashSet<AiChangeTargetKind> { AiChangeTargetKind.InstructionStep, AiChangeTargetKind.InstructionGroup };

    /// <remarks>
    /// Tags are the recipe's framing rather than its method, so they belong with the metadata a scope of that
    /// name is understood to cover.
    /// </remarks>
    private static readonly IReadOnlySet<AiChangeTargetKind> MetadataTargets =
        new HashSet<AiChangeTargetKind> { AiChangeTargetKind.Recipe, AiChangeTargetKind.Tag };

    private static readonly IReadOnlySet<AiChangeTargetKind> MediaTargets =
        new HashSet<AiChangeTargetKind> { AiChangeTargetKind.AssetLink };

    /// <summary>An undeclared scope permits nothing, so a missing scope fails closed.</summary>
    private static readonly IReadOnlySet<AiChangeTargetKind> NoTargets = new HashSet<AiChangeTargetKind>();

    /// <summary>
    /// How many times one operation may be claimed before it is abandoned for good.
    /// </summary>
    /// <remarks>
    /// <strong>The bound the lease-recovery edge requires.</strong> Returning a timed-out <c>Running</c>
    /// operation to <c>Requested</c> lets a creator's request survive a worker crash, and without a counter it
    /// also lets a task that kills its worker every time cycle between those two states forever, spending a
    /// provider budget each pass. Three: enough to ride out a deployment restart, few enough that a
    /// reproducible crash stops being retried before it costs anything worth noticing.
    /// </remarks>
    public const int MaxAttempts = 3;

    /// <summary>How many operations one claim pass considers.</summary>
    public const int ClaimBatchSize = 20;

    /// <summary>
    /// How long a worker holds a claim before another may take it.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than the provider attempt timeout plus its retries, so a slow generation is not
    /// stolen from the worker still legitimately waiting on it. A worker that expects to exceed this renews.
    /// </remarks>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>How long a queued request may wait before nobody is going to run it.</summary>
    public static readonly TimeSpan RequestTimeToLive = TimeSpan.FromHours(6);

    /// <summary>The fewest photography concepts IMG-001 may answer with.</summary>
    /// <remarks>
    /// One, not two. A concept here is a whole shoot plan rather than a recipe pitch, so a single strong answer
    /// is a legitimate result for a tightly specified brief — where <see cref="MinConceptCount"/> is two
    /// because "give me options" is the entire point of AIREC-001.
    /// </remarks>
    public const int MinPhotographyConceptCount = 1;

    /// <summary>The most photography concepts IMG-001 may answer with.</summary>
    /// <remarks>
    /// Three, against <see cref="MaxConceptCount"/>'s five, because each concept carries a shot list — five
    /// concepts of three shots each is fifteen frames to read through to make one choice.
    /// </remarks>
    public const int MaxPhotographyConceptCount = 3;

    /// <summary>The fewest shots one concept may plan. A concept with no frame is not a concept.</summary>
    public const int MinPhotographyShotCount = 1;

    /// <summary>The most shots one concept may plan.</summary>
    public const int MaxPhotographyShotCount = 3;

    /// <summary>The most props one shot may list.</summary>
    /// <remarks>
    /// Eight is already a crowded table. The cap also keeps the joined value inside
    /// <see cref="ChangeValueMaxLength"/> when the handler stores the list as one row.
    /// </remarks>
    public const int MaxPhotographyPropCount = 8;

    /// <summary>The longest a prose photography field may be — framing, lighting, surface, styling.</summary>
    public const int PhotographyFieldMaxLength = 600;

    /// <summary>The longest a short photography field may be — label, mood, palette.</summary>
    public const int PhotographyShortFieldMaxLength = 300;

    /// <summary>The longest one prop phrase may be.</summary>
    public const int PhotographyPropMaxLength = 120;

    /// <summary>
    /// The longest the creator's own concept description may be on an IMG-001 request.
    /// </summary>
    /// <remarks>
    /// Longer than <see cref="BriefFieldMaxLength"/> because this is the one field where a creator describes a
    /// picture they already have in mind, in their own words, and a photograph is easier to describe than to
    /// name.
    /// </remarks>
    public const int PhotographyCreatorConceptMaxLength = 1000;

    /// <summary>The longest one scene or style override may be on an IMG-001 request.</summary>
    public const int PhotographyOverrideMaxLength = 300;

    /// <summary>The most scene or style overrides one IMG-001 request may carry, per list.</summary>
    public const int MaxPhotographyOverrideCount = 10;

    /// <summary>
    /// The longest a composed image prompt may be.
    /// </summary>
    /// <remarks>
    /// Matched to <c>ContentPolicy.PromptTextMaxLength</c>, because the creator's edit of this text is what
    /// PRM-001 stores: a prompt the library cannot hold would be composed, shown and then refused on save.
    /// </remarks>
    public const int ImagePromptMaxLength = 4000;

    /// <summary>The shortest a composed image prompt may be. A one-line answer is not a prompt.</summary>
    public const int ImagePromptMinLength = 40;

    /// <summary>The most warnings one composed prompt may carry.</summary>
    public const int MaxImagePromptWarnings = 6;

    /// <summary>The most negative-guidance phrases one prompt may carry.</summary>
    public const int MaxImagePromptAvoidCount = 12;

    /// <summary>The longest one negative-guidance phrase may be.</summary>
    public const int ImagePromptAvoidMaxLength = 120;

    /// <summary>
    /// The longest an authorized brief may be once read, before it is cut.
    /// </summary>
    /// <remarks>
    /// A brief is a creator-uploaded document and can be long; what reaches the prompt is bounded so one
    /// document cannot crowd out the concept, the recipe and the guide it is supposed to be read beside.
    /// </remarks>
    public const int ImagePromptBriefMaxLength = 4000;

    /// <summary>
    /// The fewest observations a reference-image reading may carry (IMG-004).
    /// </summary>
    /// <remarks>
    /// Two rather than one, because a single observation is not a reading of a photograph — it is one remark
    /// about it, and the prompt composed beside it would have nothing reviewable behind it. A model that can
    /// see the image can say at least this much; one that cannot should be failing, not answering thinly.
    /// </remarks>
    public const int MinReferenceImageObservations = 2;

    /// <summary>
    /// The most observations one reading may carry.
    /// </summary>
    /// <remarks>
    /// Equal to the number of aspects there are, because at most one observation may be made per aspect —
    /// so this is the ceiling the shape already implies, stated so a count check can refuse a long list
    /// before the duplicate check walks it.
    /// </remarks>
    public const int MaxReferenceImageObservations = 7;

    /// <summary>The longest one observation of a reference image may be.</summary>
    /// <remarks>
    /// A sentence or two about one property of one photograph. The same bound a concept's short fields carry,
    /// because a creator reads these the same way — in a column, beside the thing they describe.
    /// </remarks>
    public const int ReferenceImageObservationMaxLength = 300;

    /// <summary>
    /// The longest the note fencing an attached reference image may be.
    /// </summary>
    /// <remarks>
    /// The note is content-free by construction — a media type, a byte count, a frame count — so this is a
    /// guard rather than a budget. It exists because the note is assembled from stored metadata, and a
    /// stored media type is a string this module did not write.
    /// </remarks>
    public const int ReferenceImageNoteMaxLength = 300;

    /// <summary>
    /// How long a proposal waits for the creator before it expires.
    /// </summary>
    /// <remarks>
    /// Generous, because reviewing a proposal is creative work a creator returns to. Its real purpose is that
    /// a proposal computed against a version the recipe has long since moved past cannot be applied anyway,
    /// so leaving it open indefinitely offers the creator something that would only fail on acceptance.
    /// </remarks>
    public static readonly TimeSpan ProposalTimeToLive = TimeSpan.FromDays(14);

    /// <summary>
    /// How long a requeued operation waits before another worker may claim it.
    /// </summary>
    /// <remarks>
    /// Backoff with the attempt count, so a task that crashes its worker is not re-claimed instantly by the
    /// next one. Jitter is deliberately absent: unlike the provider pipeline, claims are already spread by
    /// the polling interval and by which worker gets there first.
    /// </remarks>
    public static TimeSpan RequeueDelayFor(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Max(attempts - 1, 0)), 600));

    /// <summary>How often the worker checks for due operations. The same cadence <c>OutboxPolicy</c> uses.</summary>
    public static readonly TimeSpan WorkerPollingInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the maintenance sweep (lease recovery, request/proposal expiry) runs. Comfortably more often
    /// than <see cref="LeaseDuration"/> so an abandoned lease is recovered promptly, without polling on every
    /// claim tick for work that is rare by comparison.
    /// </summary>
    public static readonly TimeSpan MaintenancePollingInterval = TimeSpan.FromMinutes(1);
}
