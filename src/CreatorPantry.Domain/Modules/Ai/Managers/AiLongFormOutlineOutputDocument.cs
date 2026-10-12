using System.Globalization;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract LF.2.1's answer must satisfy: an ordered plan of sections for one long-form piece, and nothing
/// else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A plan, not prose.</strong> Each section is a role, a heading and one line saying what it is for.
/// There is no field for a body, a word count, a figure or a verdict, and a model that returned one fails
/// strict shape validation as an unknown field. Prose arrives later, one section at a time (LF.3.1), after the
/// creator has approved the plan.
/// </para>
/// <para>
/// <strong>No "written from" block</strong>, for the reason <see cref="AiImagePromptOutputDocument"/> records:
/// what grounded an answer is the server's fact, kept as the proposal's provenance.
/// </para>
/// </remarks>
public sealed record AiLongFormOutlineOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>The sections, in the order the piece should read.</summary>
    public IReadOnlyList<AiLongFormOutlineSection> Sections { get; init; } = [];

    /// <summary>What the creator should know about this plan, including anything that could not be honoured.</summary>
    public IReadOnlyList<AiLongFormOutlineWarning> Warnings { get; init; } = [];
}

/// <summary>One planned section.</summary>
public sealed record AiLongFormOutlineSection
{
    /// <summary>
    /// The section's role, by the exact name the task listed.
    /// </summary>
    /// <remarks>
    /// A string rather than the enum, so a name outside the vocabulary is refused with a reason of its own
    /// instead of as a shape failure, and a number is never taken for a role.
    /// </remarks>
    public required string Role { get; init; }

    /// <summary>The heading a reader would see, on one line.</summary>
    public required string Heading { get; init; }

    /// <summary>One line saying what the section is for. Never the section itself.</summary>
    public required string Intent { get; init; }

    /// <summary>
    /// For a newsletter item, the position — counting from 1 — of the recipe it covers in the list of recipes
    /// the task was shown. Null for every other section.
    /// </summary>
    /// <remarks>
    /// A position because no identifier reaches a model. The server turns it into the reference it stands for.
    /// </remarks>
    public int? Source { get; init; }
}

/// <summary>One thing to tell the creator about the plan.</summary>
public sealed record AiLongFormOutlineWarning
{
    public required AiWarningKind Kind { get; init; }

    /// <summary>The section it is about, counting from 0, or null when it is about the plan as a whole.</summary>
    public int? SectionIndex { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// The JSON Schema a model is shown for LF.2.1, generated from <see cref="AiLongFormOutlineOutputDocument"/>
/// rather than hand-written, so the shape asked for and the shape judged cannot drift apart.
/// </summary>
public static class AiLongFormOutlineOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiLongFormOutlineOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}

/// <summary>The field names an LF.2.1 planned section's rows are stored under.</summary>
/// <remarks>
/// Stable, because a stored proposal's rows are read back by whatever turns them into a draft revision (LF.2.2).
/// </remarks>
public static class LongFormPlanFields
{
    /// <summary>The role, as its stored number: <see cref="LongFormSectionRole"/> is append-only.</summary>
    public const string Role = "role";

    public const string Intent = "intent";

    /// <summary>For a newsletter item, the creative-context reference of the recipe it covers.</summary>
    public const string SourceReferenceId = "sourceReferenceId";
}

/// <summary>The field names a recipe pin's rows are stored under, beside a long-form plan.</summary>
public static class LongFormPinFields
{
    public const string RecipeId = "recipeId";

    public const string RecipeVersionId = "recipeVersionId";
}

/// <summary>The keys an LF.2.1 request travels under in <c>AiOperation.TaskInputsJson</c>.</summary>
public static class LongFormOutlineInputs
{
    public const string CreativeContextId = "creativeContextId";

    /// <summary>A <see cref="LongFormKind"/> by name.</summary>
    public const string Kind = "kind";

    /// <summary>The length the creator asked for, in words. Absent when they left it to the kind's own target.</summary>
    public const string TargetWords = "targetWords";

    /// <summary>
    /// The roles the creator already wants, by name, joined by <see cref="PhotographyConceptInputs.ListSeparator"/>.
    /// </summary>
    /// <remarks>
    /// Roles and never headings: a role is a word from a closed vocabulary, so it can be told to a model in the
    /// task without a word of creator text entering the instruction segment. A heading is the creator's to
    /// write on the plan itself.
    /// </remarks>
    public const string WantedRoles = "wantedRoles";

    /// <summary>
    /// The draft the plan is for, on a request made for a draft that already exists. Absent on the request
    /// that starts a draft, whose own id is the draft's.
    /// </summary>
    /// <remarks>
    /// Absent there on purpose: the inputs are what an idempotent replay is compared against, and a draft id
    /// minted per attempt would make every retry of one request look like a different one.
    /// </remarks>
    public const string LongFormDraftId = "longFormDraftId";

    public static Guid? ReadContextId(IReadOnlyDictionary<string, string>? inputs) =>
        PhotographyConceptInputs.Read(inputs, CreativeContextId) is { } value && Guid.TryParse(value, out var id)
            ? id
            : null;

    /// <summary>The draft an outline request is for: the one it names, or the one it started.</summary>
    public static Guid ReadDraftId(IReadOnlyDictionary<string, string>? inputs, Guid operationId) =>
        PhotographyConceptInputs.Read(inputs, LongFormDraftId) is { } value && Guid.TryParse(value, out var id)
            ? id
            : operationId;

    public static LongFormKind? ReadKind(IReadOnlyDictionary<string, string>? inputs) =>
        TryRole<LongFormKind>(PhotographyConceptInputs.Read(inputs, Kind), out var kind) ? kind : null;

    /// <summary>The requested length; null when absent. Unreadable is reported as zero, which no request may ask for.</summary>
    public static int? ReadTargetWords(IReadOnlyDictionary<string, string>? inputs) =>
        PhotographyConceptInputs.Read(inputs, TargetWords) is not { } value
            ? null
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var words) ? words : 0;

    /// <summary>The wanted roles, or null when any one of them is not a role at all.</summary>
    public static IReadOnlyList<LongFormSectionRole>? ReadWantedRoles(IReadOnlyDictionary<string, string>? inputs)
    {
        var roles = new List<LongFormSectionRole>();

        foreach (var name in PhotographyConceptInputs.ReadList(inputs, WantedRoles))
        {
            if (!TryRole<LongFormSectionRole>(name, out var role))
            {
                return null;
            }

            roles.Add(role);
        }

        return roles;
    }

    /// <summary>
    /// A member by its exact name, and nothing else.
    /// </summary>
    /// <remarks>
    /// Matched against the names rather than parsed: <c>Enum.TryParse</c> takes "99" for a value and
    /// "Hook,Context" for the two combined, and either would let text that names no role stand for one.
    /// </remarks>
    internal static bool TryRole<TEnum>(string? name, out TEnum value)
        where TEnum : struct, Enum
    {
        if (name is not null && Array.IndexOf(Enum.GetNames<TEnum>(), name) >= 0)
        {
            value = Enum.Parse<TEnum>(name);

            return true;
        }

        value = default;

        return false;
    }
}
