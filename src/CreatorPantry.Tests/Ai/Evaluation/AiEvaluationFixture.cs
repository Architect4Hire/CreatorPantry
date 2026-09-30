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
