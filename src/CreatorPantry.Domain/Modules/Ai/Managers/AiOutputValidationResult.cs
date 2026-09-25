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
