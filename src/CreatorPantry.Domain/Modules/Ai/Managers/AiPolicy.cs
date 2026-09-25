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
}
