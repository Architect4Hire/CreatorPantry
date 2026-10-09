using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>The property this task's own SCOPE line names, and what a fixture demonstrates about it.</summary>
public enum AiEvaluationCategory
{
    SchemaValidity,
    RefusalSafety,
    WorkspaceIsolation,
    PromptInjection,
    StaleSource,
    DeterministicToolRouting,
    ProposalAcceptance,
}

/// <summary>Which pipeline entry point a fixture's <c>input</c>/<c>expect</c> shape is written for.</summary>
public enum AiEvaluationKind
{
    /// <summary><see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiOutputValidator.Validate"/> directly.</summary>
    OutputValidation,

    /// <summary><see cref="CreatorPantry.Domain.Modules.Ai.Managers.PromptEnvelopeBuilder"/> directly.</summary>
    PromptEnvelope,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiDiffCalculator"/> and
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiProposalAssembler"/> together.
    /// </summary>
    ProposalAssembly,

    /// <summary>The full SQLite-backed <c>IAiOperationWorker</c> pipeline, against a fake provider.</summary>
    WorkerOperation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiConceptOutputValidator.Validate"/> directly. A
    /// capability whose answer is not a recipe diff has its own document and its own domain rules, so its
    /// SchemaValidity fixtures cannot run through <see cref="OutputValidation"/>'s case runner.
    /// </summary>
    ConceptOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiRecipeDraftOutputValidator.Validate"/> directly,
    /// for AIREC-002's own document and its own domain rules — the same reason <see cref="ConceptOutputValidation"/>
    /// exists apart from <see cref="OutputValidation"/>.
    /// </summary>
    RecipeDraftOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiSubstitutionOutputValidator.Validate"/> directly,
    /// for AIREC-004's own document.
    /// </summary>
    /// <remarks>
    /// The domain rules this reaches are the ones that carry AIREC-004's restriction — ranked alternatives,
    /// evidence that stays unknown, an allergen consequence that arrives with its caution, and an empty answer
    /// that explains itself. They are deterministic, which is exactly what a fixture can demonstrate; whether
    /// a model's advice is culinarily *good* is not, and no fixture here claims otherwise.
    /// </remarks>
    SubstitutionOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiAdaptationOutputValidator.Validate"/> directly,
    /// for AIREC-005's two additional checks over the shared recipe-diff document
    /// <see cref="OutputValidation"/> already covers.
    /// </summary>
    /// <remarks>
    /// A fixture naming a yield goal may also declare deterministic scaling inputs (a multiplier and a small
    /// set of ingredient lines), which the case runner resolves through the real
    /// <c>RecipeScalingCalculator</c> before validating — so a fixture's expected figure is never hand-typed
    /// arithmetic that could itself be wrong.
    /// </remarks>
    AdaptationOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiRecipeReviewOutputValidator.Validate"/> directly,
    /// for AIREC-006's own document.
    /// </summary>
    /// <remarks>
    /// The domain rules this reaches carry AIREC-006's restriction — an unsupported claim that says what is
    /// unknown, a reference-check flag that cannot be skipped on an allergen, dietary, or critical finding, an
    /// allergen or dietary finding that arrives with its caution and its severity floor, and an empty answer
    /// that explains itself. They are deterministic, which is exactly what a fixture can demonstrate; whether a
    /// model's review is culinarily thorough is not, and no fixture here claims otherwise.
    /// </remarks>
    RecipeReviewOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiEditorialPackageOutputValidator.Validate"/> and then
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiEditorialClaimScanner"/> directly, for RCPUB-001's own
    /// document.
    /// </summary>
    /// <remarks>
    /// A fixture may declare the recipe's facts (the numbers it states, what its creator wrote, its storage
    /// notes) and the codes the scanner must report, so "an invented quantity becomes a warning" is demonstrated
    /// against the real scanner rather than asserted in prose. Deterministic only: whether the prose is good is
    /// not something a fixture can show.
    /// </remarks>
    EditorialPackageOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiSeoPackageOutputValidator.Validate"/> under the default
    /// <c>SeoRules</c> and then <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiSeoClaimScanner"/>, for
    /// RCPUB-002's own document. A fixture may declare the recipe's facts, the assets' captions, and the codes the
    /// scanner must report, so "an invented metric becomes a warning" is shown against the real scanner.
    /// </summary>
    SeoPackageOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Managers.Reference.SeoSlug.FromTitle"/> directly: the deterministic slug a
    /// title becomes, and that the model is never the source of one.
    /// </summary>
    SeoSlugDerivation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiBrandGuideOutputValidator.Validate"/> against a
    /// declared request context, and then
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiBrandGuideClaimScanner"/>, for 11A.17's own document.
    /// </summary>
    /// <remarks>
    /// A fixture declares what the request offered — the dimensions, the channel keys, and the passages with
    /// their ids and text — so "a citation naming a passage nobody supplied is refused" and "an answer that
    /// reproduces a passage is refused" are shown against the real validator rather than asserted in prose. It
    /// may also declare the scanner codes the answer must produce, which is how the sparse-evidence and
    /// person-reference findings are demonstrated.
    /// </remarks>
    BrandGuideOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiBrandStyleSamplesOutputValidator.Validate"/>
    /// directly, for 11A.24's own document — one half of a style test drive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fixture declares one payload, because the validator's job is one half of the comparison and both halves
    /// are held to the same rules. That symmetry is itself the thing worth fixing in place: a fixture asserting
    /// that a rule applies is asserting it applies to the column written without the guide as well as the one
    /// written with it.
    /// </para>
    /// <para>
    /// What a fixture can show is deterministic — the length floor and ceiling on each of the three samples, the
    /// hashtag that belongs only in a caption, the handle that belongs nowhere, and a warning that imitates a
    /// server finding. Whether the guided column actually sounds more like the creator is not deterministic, and
    /// no fixture here claims it is; that is what naming the applied rules and citations is for.
    /// </para>
    /// </remarks>
    BrandStyleSamplesOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiPhotographyConceptOutputValidator.Validate"/>
    /// directly, for IMG-001's own document and its own domain rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists apart from <see cref="OutputValidation"/> for the reason <see cref="ConceptOutputValidation"/>
    /// does: a photography concept is not a recipe diff, so the generic validator has nothing to say about it.
    /// </para>
    /// <para>
    /// What a fixture here can show is deterministic, and it is most of what IMG-001's restriction asks for: the
    /// concept and shot counts, exactly one hero per concept, distinct shot roles, the numeral ban and its crop-
    /// ratio exception, the claim ban, and the link/handle/markup ban. What it cannot show is whether a concept
    /// is any good to photograph, and no fixture here claims to.
    /// </para>
    /// </remarks>
    PhotographyConceptOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiImagePromptOutputValidator.Validate"/> directly,
    /// for IMG-002's own document and its own domain rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="PhotographyConceptOutputValidation"/> because the two capabilities are held to
    /// deliberately different rules: a concept may write no numeral, and a prompt may, because a prompt is the
    /// artefact an image model reads. A fixture filed under the wrong one would validate nothing.
    /// </para>
    /// <para>
    /// What a fixture here can show: the length floor and ceiling, the avoid-list bounds, the claim ban, the
    /// link/handle/markup ban, and the rendering-directive ban that is this capability's own. What it cannot
    /// show is whether the prompt describes the shot it was asked for.
    /// </para>
    /// </remarks>
    ImagePromptOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiReferenceImageOutputValidator.Validate"/>
    /// directly, for IMG-004's own document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="ImagePromptOutputValidation"/> even though both answers carry a prompt: this
    /// document also carries observations with a required confidence each, and it adds the identity and
    /// ownership bans that are this capability's own. A fixture filed under the sibling kind would run the
    /// sibling's validator and demonstrate none of them.
    /// </para>
    /// <para>
    /// What a fixture here can show: the observation count floor and ceiling, the refusal of a duplicated
    /// aspect, the refusal of an undeclared aspect or confidence, the identity and ownership bans, and the
    /// claim, link and rendering-directive bans inherited from the sibling. What it cannot show is whether an
    /// observation is true of the attached photograph, whether the confidence stated is the right one, or
    /// whether the model declined an instruction photographed into the image — the last needs a real vision
    /// call, which this harness deliberately never makes.
    /// </para>
    /// </remarks>
    ReferenceImageOutputValidation,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiDishFacetsOutputValidator.Validate"/> directly,
    /// for the dish-name reading's own document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The domain rules this reaches are the ones that keep a reading a reading: each facet answered at most
    /// once, a code that arrives with a confidence and an absent code that arrives with a reason instead, and
    /// every suggestion saying which words led to it. Those are the checks standing between "the name says
    /// grilled" and a guess that looks identical on screen, so they are worth fixing in place.
    /// </para>
    /// <para>
    /// What this kind deliberately <strong>cannot</strong> show is that an off-catalogue code is discarded.
    /// That check needs the vocabulary the handler read, which this runner has no access to — it is
    /// <c>DishFacetSuggestionHandlerTests</c>'s to prove, against a fake facade. A fixture here asserting it
    /// would be asserting nothing.
    /// </para>
    /// </remarks>
    DishFacetsOutputValidation,
}

/// <summary>
/// One versioned evaluation fixture: a scenario against the AI foundation, and the outcome it must produce.
/// </summary>
/// <remarks>
/// Reuses <see cref="PromptTemplateVersion"/> for <see cref="Version"/> rather than inventing a parallel
/// major.minor.patch type — a fixture is a versioned artifact loaded at startup for the same reason a prompt
/// template is, and the two should not drift into two ways of spelling the same concept.
/// </remarks>
public sealed record AiEvaluationFixture(
    string Id,
    PromptTemplateVersion Version,
    AiEvaluationCategory Category,
    AiEvaluationKind Kind,
    string Description,
    JsonElement Input,
    JsonElement Expect)
{
    public string Identity => $"{Id}-{Version}";
}

/// <summary>A fixture that cannot be trusted to describe a scenario: malformed, mis-declared, or duplicated.</summary>
/// <remarks>
/// Mirrors <see cref="PromptTemplateException"/>'s reasoning exactly: a fixture is read once, at harness
/// start, so a bad one is a fail-fast authoring error, not a per-run runtime condition.
/// </remarks>
public sealed class AiEvaluationException : Exception
{
    public AiEvaluationException(string origin, string message)
        : base($"AI evaluation fixture '{origin}': {message}") => Origin = origin;

    public string Origin { get; }
}
