namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Which AI capability an operation is running.</summary>
/// <remarks>
/// <para>
/// Deliberately almost empty. The recipe capabilities — concepts, rewrites, substitutions, scaling review —
/// arrive with the prompts that define them, and inventing their names here before a single one exists would
/// fix a vocabulary nothing has had to live with yet.
/// </para>
/// <para>
/// A task type is chosen by the server from an allow-list, never taken from a request field: the client picks
/// a discriminator the API recognises, and the API maps it here.
/// </para>
/// </remarks>
public enum AiTaskType
{
    /// <summary>
    /// Not declared. Never valid on a stored row — the check constraint refuses it — so that a row which
    /// forgot to say what it was doing cannot pass for the first real member of this enum.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// An inert task used to exercise the operation lifecycle end to end without calling a model. It is what
    /// the first generic proposal endpoint is wired to before any real capability is enabled.
    /// </summary>
    Diagnostic = 1,

    /// <summary>
    /// AIREC-001: proposes several distinct recipe concepts from a creator's structured brief — audience,
    /// course, cuisine, dietary goals, available ingredients, exclusions, equipment, skill, season, time
    /// budget, and creator style. Names no recipe: the brief is the source, not a pinned version.
    /// </summary>
    RecipeConcepts = 2,

    /// <summary>
    /// AIREC-002: proposes one complete structured first draft — title, description, yield, timing,
    /// ingredient groups, ordered instructions, equipment, notes, and unresolved questions — from a selected
    /// concept or declared brief fields. Names no recipe: like <see cref="RecipeConcepts"/>, there is nothing
    /// yet to pin a version against.
    /// </summary>
    RecipeFirstDraft = 3,

    /// <summary>
    /// AIREC-003: proposes scoped changes to one pinned version of an existing recipe, in the sections the
    /// creator selected and towards the goal they stated, with a rationale for each.
    /// </summary>
    /// <remarks>
    /// The first task to <em>revise</em> rather than originate: it names a recipe and a version, so it is the
    /// recipe-bound lifecycle's own shape — a server-computed diff against the pinned source, reviewed through
    /// the proposal panel and applied through the recipe module's facade.
    /// </remarks>
    RecipeRevision = 4,

    /// <summary>
    /// AIREC-004: ranked alternatives for one ingredient the creator selected in a pinned version, with
    /// quantity guidance, technique/flavour/texture impact, dietary and allergen consequences, confidence,
    /// evidence, and what to test before trusting any of it.
    /// </summary>
    /// <remarks>
    /// <strong>Names a recipe and proposes no change to it</strong>, which no earlier task does.
    /// <see cref="RecipeConcepts"/> and <see cref="RecipeFirstDraft"/> propose content with no recipe to
    /// change; <see cref="RecipeRevision"/> proposes changes to one. This reads a recipe to understand what an
    /// ingredient is doing in it, and answers with advice a creator acts on themselves — hence
    /// <see cref="AiOperationScope.Advisory"/>, which permits no change to reach the recipe at all.
    /// </remarks>
    IngredientSubstitution = 5,

    /// <summary>
    /// AIREC-005: adapts one pinned version of an existing recipe toward exactly one declared goal — dietary,
    /// equipment, yield, or skill level — returning a complete cross-field proposal across ingredients, method,
    /// timing, texture, safety, and yield implications.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Shaped like <see cref="RecipeRevision"/>, not like <see cref="IngredientSubstitution"/>.</strong>
    /// This is a multi-field edit, so it runs in <see cref="AiOperationScope.WholeRecipe"/> — fixed server-side,
    /// never chosen by the client — and its answer is an ordinary <see cref="AiOutputDocument"/> diffed against
    /// the pinned snapshot by <see cref="AiDiffCalculator"/>, exactly as a revision's is. The goal is a request
    /// field, not a fifth document shape: it is the discriminator <see cref="AiAdaptationOutputValidator"/> and
    /// the prompt condition on, the way a revision conditions on its scope.
    /// </para>
    /// <para>
    /// <strong>Its one restriction a revision does not share: deterministic yield math.</strong> When the goal
    /// is yield, the handler computes the scaled quantities through <c>RecipeScalingCalculator</c> before the
    /// model is ever called, and <see cref="AiAdaptationOutputValidator"/> rejects an answer whose ingredient
    /// quantities disagree with that computation — the model may explain what a larger batch changes about the
    /// method, but it may never do the arithmetic itself (ai.md).
    /// </para>
    /// <para>
    /// An impossible or unsafe goal is answered honestly: <see cref="AiWarningKind.Limitation"/> is what an
    /// answer proposing little or nothing toward the goal is required to carry, rather than a proposal that
    /// only appears to meet it.
    /// </para>
    /// </remarks>
    RecipeAdaptation = 6,

    /// <summary>
    /// AIREC-006: field-linked findings about one pinned version of an existing recipe — completeness,
    /// consistency, timing, temperature, ambiguous steps, unused ingredients, likely failures, allergen and
    /// dietary conflicts, and unsupported claims — each with a severity, an evidence basis, and whether it
    /// needs checking against a reference the system does not have.
    /// </summary>
    /// <remarks>
    /// <strong>Reads a recipe and proposes no change to it</strong>, the same shape
    /// <see cref="IngredientSubstitution"/> has: <see cref="AiOperationScope.Advisory"/>, and
    /// <see cref="AiChangeTargetKind.RecipeReviewFinding"/> is absent from <see cref="AiChangeApplicability"/>
    /// so no stored finding can become an edit. A finding is a review signal a creator judges, never a
    /// certification and never a rewrite.
    /// </remarks>
    RecipeReview = 7,

    /// <summary>
    /// AIREC-008: a concise, creator-facing explanation of an existing, already-persisted proposal — one item
    /// per changed target plus any verification needs, each linked back to the actual
    /// <c>AiStructuredChange</c>/<c>AiWarning</c> rows it describes.
    /// </summary>
    /// <remarks>
    /// <strong>Reads a proposal, not a recipe, and proposes no change to either.</strong> Every earlier task
    /// takes a recipe (or nothing) as its source; this is the first to take another operation's own stored
    /// proposal. It runs entirely through <see cref="AiProposalExplanationAiTaskHandler"/>'s deterministic
    /// templating — no model is called — so an explanation item can state only what the source proposal's own
    /// rows already record, never a new fact. <see cref="AiOperationScope.Advisory"/>, and
    /// <see cref="AiChangeTargetKind.ProposalExplanationItem"/> is absent from
    /// <see cref="AiChangeApplicability"/>, the same guarantee <see cref="RecipeReview"/> makes.
    /// </remarks>
    ProposalExplanation = 8,

    /// <summary>
    /// RCPUB-001: headnote, introduction, tips, substitutions, storage/reheating, FAQ and call to action for one
    /// approved recipe version. Originates prose and proposes no change to the recipe, so it runs at
    /// <see cref="AiOperationScope.Advisory"/> and <see cref="AiChangeTargetKind.ContentSection"/> is absent from
    /// <see cref="AiChangeApplicability"/> — the same guarantee <see cref="RecipeReview"/> makes. A derivative
    /// is a <c>ContentRevision</c> the creator accepts, never a recipe edit.
    /// </summary>
    EditorialPackage = 9,

    /// <summary>
    /// RCPUB-002: search title, slug, meta description, key phrases, alt-text suggestions and internal-link ideas
    /// for one approved recipe version. Advisory for the same reason <see cref="EditorialPackage"/> is: it
    /// originates copy and proposes no change to the recipe, and it reuses
    /// <see cref="AiChangeTargetKind.ContentSection"/>, which has no path to a recipe edit.
    /// </summary>
    SeoPackage = 10,

    /// <summary>
    /// 11A.17: proposes structured brand-guide guidance — voice, tone, tenor, style, language, channel, blog,
    /// social and visual direction — from the creator's own guide answers and the source document versions they
    /// selected, with a citation behind every claim and its uncertainties stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The first task that names neither a recipe nor anything derived from one.</strong> Its subject is
    /// a <c>BrandStyleGuide</c>, so <c>RecipeId</c> and <c>RecipeVersionId</c> are both null and the scope is
    /// <see cref="AiOperationScope.NotApplicable"/> — the reading <see cref="RecipeConcepts"/> uses, not
    /// <see cref="AiOperationScope.Advisory"/>, whose own remarks require a recipe and a pinned version to be
    /// present.
    /// </para>
    /// <para>
    /// <strong>A proposal, never an active guide.</strong> <see cref="AiChangeTargetKind.BrandGuideSection"/> is
    /// absent from <see cref="AiChangeApplicability"/> and answers <c>null</c> in
    /// <see cref="AiChangeTargetPolicy"/>, so no stored row can reach a recipe; a guide version is immutable, so
    /// nothing here edits one; and nothing in the brand module reads an <c>AiStructuredChange</c>.
    /// </para>
    /// <para>
    /// <strong>What a creator may do with it is accept it (11A.18), and that writes a draft.</strong> They name
    /// the items they agree with, rewriting any whose wording they would rather own, and the acceptance seam
    /// writes one new version through the brand module's facade — subject to every rule a version they typed
    /// would obey, refused outright if the guide has been edited since the proposal was composed, and citing
    /// each source at the version the proposal actually read. The version is a draft: becoming the workspace's
    /// default still needs an approval and an Owner, which are separate decisions on separate routes.
    /// </para>
    /// <para>
    /// <strong>Every claim is cited or it is not stored.</strong> Grounding is the passages of the selected
    /// versions' current chunk sets, read through the brand module's facade; the validator refuses a citation
    /// naming a passage this request did not offer, and the handler refuses an answer that copies a long run of
    /// one. Thin and contradictory evidence are stated by the server rather than left to the model to mention.
    /// </para>
    /// </remarks>
    BrandGuideProposal = 11,

    /// <summary>IMG-001: a structured photography concept. Handler lands in Phase 12; brand visual context (11A.21) is ready for it.</summary>
    PhotographyConcept = 12,

    /// <summary>IMG-002: an editable final image prompt. Handler lands in Phase 12.</summary>
    ImagePrompt = 13,

    /// <summary>
    /// 11A.24: two short samples each of a blog introduction, a social caption and an image prompt — one set
    /// written with no brand context at all, one grounded in a guide version the creator selected — so a
    /// creator can see what their guide does before they rely on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The first task that calls the provider twice for one operation</strong>, and the comparison is
    /// the reason. One call holding the guide while writing both halves would produce a "without your guide"
    /// sample written with it, and the screen would then be claiming something untrue about its own left-hand
    /// column. <see cref="AiTaskHandlerOutcome.Attempts"/> is already a list and
    /// <see cref="Data.Entities.AiExecutionMetadata"/> is already one row per attempt, so both calls record
    /// themselves against this operation without anything being invented to hold them.
    /// </para>
    /// <para>
    /// <strong>The first task that consumes a <see cref="BrandContextPackage"/>.</strong> 11A.19 built the
    /// assembler and nothing used it. This names a guide and a version number through
    /// <see cref="BrandContextRequestInputs"/>, so the guided half is grounded on the exact version the creator
    /// chose — and a selection that does not resolve is refused rather than quietly replaced by the active
    /// guide. An unapproved or non-active version is a legitimate thing to try out, and arrives as a
    /// <see cref="BrandContextConflict"/> rather than a refusal.
    /// </para>
    /// <para>
    /// <strong>It writes nothing, and can write nothing.</strong> Like <see cref="BrandGuideProposal"/> it
    /// names no recipe, so the scope is <see cref="AiOperationScope.NotApplicable"/>; unlike it, there is no
    /// acceptance seam at all. <see cref="AiChangeTargetKind.BrandStyleSampleWithoutGuide"/> and
    /// <see cref="AiChangeTargetKind.BrandStyleSampleWithGuide"/> are both absent from
    /// <see cref="AiChangeApplicability"/> and both answer <c>null</c> in
    /// <see cref="AiChangeTargetPolicy"/>, and no route accepts, disposes of or applies one. A sample is
    /// something to read.
    /// </para>
    /// <para>
    /// <strong>Which rules applied is the server's answer, never the model's.</strong> The guidance that
    /// reached the prompt is what <see cref="BrandContextSelection.SectionKeysFor"/> selected, and the passages
    /// cited are the ones the package carried. Asking the model which of the creator's rules it had followed
    /// would invite exactly the unfalsifiable "this sounds more like you" that this capability exists to
    /// replace.
    /// </para>
    /// </remarks>
    BrandStyleTestDrive = 14,

    /// <summary>
    /// IMG-004: structured visual observations of a reference image the creator supplied, plus an editable
    /// prompt that would photograph something like it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The only task that sends a model anything but text.</strong> The image rides on
    /// <see cref="PromptEnvelope.Images"/> behind the <c>REFERENCE_IMAGE</c> fence at
    /// <see cref="PromptSegmentTrust.Untrusted"/> trust, and the fence's own remarks are honest about the
    /// limit: a fence delimits text, and a photograph of a note reading "ignore your instructions" arrives as
    /// pixels no delimiter can wrap. What the structure buys is that the image is declared as material to
    /// describe; whether a model obeys writing it finds inside one is behaviour the evaluation set examines.
    /// </para>
    /// <para>
    /// <strong>It describes and infers nothing.</strong> Every observation carries its own
    /// <see cref="AiReferenceImageConfidence"/>, because "the surface is probably unglazed ceramic" and "the
    /// surface is unglazed ceramic" are different statements about someone else's photograph. The
    /// RESTRICTION this ships under names four things it may not do — infer an ingredient it cannot see,
    /// assert who owns a brand in frame, identify a person, or state a food-safety fact — and the first is the
    /// one no validator can catch, so it is the template's instruction and the evaluation set's question.
    /// </para>
    /// <para>
    /// <strong>It changes no recipe.</strong> Its <see cref="AiChangeTargetKind"/> is absent from
    /// <c>AiChangeApplicability</c>, like <see cref="PhotographyConcept"/>'s and
    /// <see cref="ImagePrompt"/>'s, so there is no code path from a stored row to a recipe edit.
    /// </para>
    /// </remarks>
    ReferenceImageAnalysis = 15,
}
