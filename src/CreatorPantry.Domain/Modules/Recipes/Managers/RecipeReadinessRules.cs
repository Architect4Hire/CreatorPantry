namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What one rule concluded about one recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Four answers, not two.</strong> A checklist has to distinguish "checked and fine" from "there was
/// nothing to check" — a recipe that cites no source has not passed the attribution rule, the rule did not
/// apply to it — and a reader must not have to infer either from a rule's absence. Returning every rule's
/// verdict is also what makes the result usable as the screen it is for.
/// </para>
/// <para>
/// Not persisted: an evaluation writes nothing (TESTRUN-004). Numbering starts at one so a value nobody
/// assigned is out of range and fails loudly rather than defaulting to a verdict about a recipe.
/// </para>
/// </remarks>
public enum RecipeReadinessStatus
{
    /// <summary>The rule applied and the recipe satisfies it.</summary>
    Satisfied = 1,

    /// <summary>The rule applied, the recipe does not satisfy it, and that stands in the way of approval.</summary>
    Blocker = 2,

    /// <summary>
    /// The rule applied, the recipe does not satisfy it, and it is advice rather than a bar. A creator may
    /// approve a recipe with recommendations outstanding.
    /// </summary>
    Recommendation = 3,

    /// <summary>
    /// The rule did not apply — the attribution rule to a recipe with no source URL, say. Distinct from
    /// <see cref="Satisfied"/> because nothing was checked, and stating that is more honest than implying a pass.
    /// </summary>
    NotApplicable = 4,
}

/// <summary>
/// How severely an unmet rule is reported. The configurable half of the catalogue.
/// </summary>
/// <remarks>
/// A separate enum from <see cref="RecipeReadinessStatus"/> although its two members are two of that one's four,
/// because they answer different questions: this is a rule's <em>setting</em>, that is a recipe's
/// <em>result</em>. One is what an operator may override; the other is what an evaluation produces and nobody
/// configures. Collapsing them would put <c>Satisfied</c> and <c>NotApplicable</c> in the space of things
/// configuration can set a rule to.
/// </remarks>
public enum RecipeReadinessSeverity
{
    /// <summary>Unmet stands in the way of approval.</summary>
    Blocker = 2,

    /// <summary>Unmet is advice.</summary>
    Recommendation = 3,
}

/// <summary>
/// What kind of record a finding's evidence points at.
/// </summary>
/// <remarks>
/// Named kinds rather than a free string, so that a client can link to the thing a finding is about rather than
/// print an id. <see cref="Ingredient"/> and <see cref="AiProposal"/> name records in other modules, which is
/// legitimate here precisely because a finding is a reference rather than a read — the evaluation never touches
/// either table, it is handed facts about them.
/// </remarks>
public enum RecipeReadinessEvidenceKind
{
    Recipe = 1,
    RecipeIngredientLine = 2,
    RecipeVersion = 3,
    RecipeTestRun = 4,
    TestIssue = 5,
    RecipeAssetLink = 6,
    Ingredient = 7,
    AiProposal = 8,
}

/// <summary>
/// One record, and where relevant one field of it, that caused a finding.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism behind TESTRUN-004's requirement that a rule "explain exactly which record/field caused each
/// result". A message saying "yield is missing" is not that; a message plus
/// <c>(Recipe, {id}, "YieldText")</c> is.
/// </para>
/// <para>
/// <see cref="Label"/> carries the creator's own words where there are any to carry — the ingredient line's
/// display text, the issue's title — so a reader sees the thing rather than its id. It is never invented and
/// never a normalized form: where the evidence has no creator-facing text, it is null.
/// </para>
/// </remarks>
/// <param name="FieldName">
/// The property the rule read, in the domain's own spelling, or <c>null</c> when the finding is about the
/// record's existence rather than one of its fields.
/// </param>
public sealed record RecipeReadinessEvidence(
    RecipeReadinessEvidenceKind Kind,
    Guid? RecordId,
    string? FieldName = null,
    string? Label = null);

/// <summary>
/// One rule in the readiness catalogue: its stable id and what it is called.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Id"/> is a contract.</strong> A client branches on it, configuration names it to override
/// a severity, and a stored transition record from 10.6 will cite it. Renaming one is a breaking change in the
/// sense api-contract.md means; adding one is not.
/// </para>
/// <para>
/// The default severity lives here and the effective severity does not — that is
/// <see cref="RecipeReadinessOptions"/> applied on top, reported per finding so an unexpected verdict is
/// explainable rather than mysterious.
/// </para>
/// </remarks>
/// <param name="Summary">
/// What the rule checks, in one line, for a reader who has the id and not the source. Present-tense and about
/// the rule, not about any recipe — the per-recipe sentence is built by the evaluator with the evidence in it.
/// </param>
public sealed record RecipeReadinessRule(
    string Id,
    RecipeReadinessSeverity DefaultSeverity,
    string Summary);

/// <summary>
/// Every readiness rule, with the severity each carries unless configuration says otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Versioned as a whole.</strong> <see cref="Version"/> travels on every evaluation, so a stored or
/// screenshotted result stays interpretable after the catalogue changes, and 10.6's "approval needs a fresh
/// evaluation" can require one made under the current version. Adding a rule or changing a default severity
/// raises it; fixing a typo in a summary does not.
/// </para>
/// <para>
/// <strong>Three areas the SCOPE names have no rules here, and each absence is deliberate.</strong>
/// </para>
/// <para>
/// <em>Nutrition.</em> There is no nutrition model in this product — <c>Ingredient</c>'s own remarks say
/// nutrition "carries its own provenance and uncertainty model and is modelled separately", and nothing has
/// modelled it. A rule needs a record to read, and inventing a column to give one something to check would be
/// the worse of the two outcomes. When nutrition arrives, its rule arrives with it.
/// </para>
/// <para>
/// <em>Publication metadata beyond the recipe.</em> No <c>Publication</c>, <c>SEOBrief</c> or
/// <c>ContentProject</c> exists yet, so the four <c>publication.*</c> rules read the recipe's own
/// publication-facing columns and nothing else. They are named for what they check.
/// </para>
/// <para>
/// <em>Hero media beyond the link.</em> <c>RecipeAssetLink.MediaAssetId</c> still has no foreign key, so
/// <see cref="MediaHeroMissing"/> can say a hero link exists and cannot say the asset does, or that it has alt
/// text. That is a real limit of the rule and not an oversight; media.md's alt-text requirements get their rule
/// when the media library can answer them.
/// </para>
/// </remarks>
public static class RecipeReadinessCatalogue
{
    /// <summary>
    /// The catalogue's version, reported on every evaluation. Raised when a rule is added or removed, or when a
    /// default severity changes.
    /// </summary>
    public const string Version = "1.0.0";

    // ---- Required fields ----

    public const string IngredientsPresent = "recipe.ingredients.present";

    public const string InstructionsPresent = "recipe.instructions.present";

    public const string YieldStated = "recipe.yield.stated";

    public const string TimeStated = "recipe.time.stated";

    public const string DescriptionPresent = "recipe.description.present";

    public const string AttributionPresent = "recipe.attribution.present";

    // ---- Ingredient resolution ----

    public const string IngredientsAmbiguous = "recipe.ingredients.ambiguous";

    public const string IngredientsUnrecognized = "recipe.ingredients.unrecognized";

    // ---- Outstanding AI work ----

    public const string AiSafetyCautionOutstanding = "recipe.ai.safetyCautionOutstanding";

    public const string AiWarningOutstanding = "recipe.ai.warningOutstanding";

    public const string AiChangesPending = "recipe.ai.changesPending";

    // ---- Allergen records ----

    public const string AllergensTraitUnreviewed = "recipe.allergens.traitUnreviewed";

    // ---- Test coverage ----

    public const string TestingCurrentVersionUntested = "recipe.testing.currentVersionUntested";

    public const string TestingLastOutcomeFailed = "recipe.testing.lastOutcomeFailed";

    public const string TestingBlockingIssueOutstanding = "recipe.testing.blockingIssueOutstanding";

    public const string TestingIssueOutstanding = "recipe.testing.issueOutstanding";

    // ---- Media ----

    public const string MediaHeroMissing = "recipe.media.heroMissing";

    // ---- Publication-facing metadata ----

    public const string PublicationCuisineMissing = "recipe.publication.cuisineMissing";

    public const string PublicationCourseMissing = "recipe.publication.courseMissing";

    public const string PublicationTagsMissing = "recipe.publication.tagsMissing";

    /// <summary>
    /// Every rule, in the order a creator reads them: what the recipe must say, then what is unresolved in it,
    /// then what has been tested, then what it needs to be published.
    /// </summary>
    /// <remarks>
    /// The order is the catalogue's own and not a severity ordering — a screen grouping by area gets that for
    /// free, and one sorting by severity can sort. An evaluation returns findings in this order.
    /// </remarks>
    public static readonly IReadOnlyList<RecipeReadinessRule> Rules =
    [
        new(IngredientsPresent, RecipeReadinessSeverity.Blocker,
            "The recipe has at least one ingredient line."),
        new(InstructionsPresent, RecipeReadinessSeverity.Blocker,
            "The recipe has at least one instruction step."),
        new(YieldStated, RecipeReadinessSeverity.Blocker,
            "The recipe says what it makes, in the creator's words, as a measured batch, or as a serving count."),
        new(TimeStated, RecipeReadinessSeverity.Blocker,
            "The recipe states a total time, or at least one of prep, cook and rest."),
        new(DescriptionPresent, RecipeReadinessSeverity.Recommendation,
            "The recipe has a description, which is what a listing and a share card show."),
        new(AttributionPresent, RecipeReadinessSeverity.Blocker,
            "A recipe citing a source URL also credits that source in words."),

        new(IngredientsAmbiguous, RecipeReadinessSeverity.Blocker,
            "No ingredient line is ambiguous between two or more vocabulary entries."),
        new(IngredientsUnrecognized, RecipeReadinessSeverity.Recommendation,
            "Every ingredient line is recognised, so the lines can carry dietary and allergen references."),

        new(AiSafetyCautionOutstanding, RecipeReadinessSeverity.Blocker,
            "No safety or culinary caution from an AI proposal is still unanswered."),
        new(AiWarningOutstanding, RecipeReadinessSeverity.Recommendation,
            "No other AI warning is still unanswered."),
        new(AiChangesPending, RecipeReadinessSeverity.Recommendation,
            "No AI-proposed change is still undecided."),

        new(AllergensTraitUnreviewed, RecipeReadinessSeverity.Recommendation,
            "Every recognised ingredient has reviewed allergen records — a statement about the records, never "
                + "about the food."),

        new(TestingCurrentVersionUntested, RecipeReadinessSeverity.Blocker,
            "Somebody has cooked the version as it now stands."),
        new(TestingLastOutcomeFailed, RecipeReadinessSeverity.Blocker,
            "The most recent test of the current version did not fail."),
        new(TestingBlockingIssueOutstanding, RecipeReadinessSeverity.Blocker,
            "No blocking issue found by a test of the current version is still unresolved."),
        new(TestingIssueOutstanding, RecipeReadinessSeverity.Recommendation,
            "No other issue found by a test of the current version is still unresolved."),

        new(MediaHeroMissing, RecipeReadinessSeverity.Blocker,
            "The recipe links a hero image."),

        new(PublicationCuisineMissing, RecipeReadinessSeverity.Recommendation,
            "The recipe names a cuisine."),
        new(PublicationCourseMissing, RecipeReadinessSeverity.Recommendation,
            "The recipe names a course."),
        new(PublicationTagsMissing, RecipeReadinessSeverity.Recommendation,
            "The recipe carries at least one of the workspace's tags."),
    ];

    /// <summary>Every rule by id, for the options layer and for tests that assert one rule at a time.</summary>
    public static readonly IReadOnlyDictionary<string, RecipeReadinessRule> ById =
        Rules.ToDictionary(rule => rule.Id, StringComparer.Ordinal);
}
