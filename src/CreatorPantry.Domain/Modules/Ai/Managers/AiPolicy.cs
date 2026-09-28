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
