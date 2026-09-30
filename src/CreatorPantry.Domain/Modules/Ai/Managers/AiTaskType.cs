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
}
