using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract LF.3.1's answer must satisfy: the body of one section of a long-form piece, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One section, and nowhere to put another.</strong> There is no list of sections, no heading, no
/// role, no title and no word count. The plan already says which section this is and what it is called, so a
/// model that returned a second section, renamed this one or reported its own length fails strict shape
/// validation as an unknown field.
/// </para>
/// <para>
/// <strong>No "written from" block</strong>, for the reason <see cref="AiImagePromptOutputDocument"/> records:
/// what grounded an answer is the server's fact, kept as the proposal's provenance.
/// </para>
/// </remarks>
public sealed record AiLongFormSectionOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>The section's prose, as Markdown: paragraphs, lists and emphasis. No heading of its own.</summary>
    public required string Body { get; init; }

    /// <summary>What the creator should know about this section, including anything that could not be honoured.</summary>
    public IReadOnlyList<AiLongFormSectionWarning> Warnings { get; init; } = [];
}

/// <summary>One thing to tell the creator about the section.</summary>
public sealed record AiLongFormSectionWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// The JSON Schema a model is shown for LF.3.1, generated from <see cref="AiLongFormSectionOutputDocument"/>
/// rather than hand-written, so the shape asked for and the shape judged cannot drift apart.
/// </summary>
public static class AiLongFormSectionOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiLongFormSectionOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}

/// <summary>The field names an LF.3.1 section body's rows are stored under.</summary>
/// <remarks>
/// <para>
/// Stable, because a stored proposal's rows are read back by whatever writes them into a draft revision
/// (LF.3.3).
/// </para>
/// <para>
/// <strong>A body is stored in parts.</strong> One row holds at most
/// <see cref="AiPolicy.ChangeValueMaxLength"/> characters, and a body that runs over the section ceiling is
/// returned flagged and whole, never trimmed — so it cannot be made to fit one row by cutting it.
/// <see cref="Split"/> and <see cref="Join"/> are the only two places that know how.
/// </para>
/// </remarks>
public static class LongFormSectionFields
{
    /// <summary>The revision of the draft the section was written against, as a number.</summary>
    public const string BasedOnRevision = "basedOnRevision";

    /// <summary>
    /// The parts of accepted editorial copy the body was written from, by name, separated by
    /// <see cref="ReusedPartsSeparator"/>. Absent when there were none.
    /// </summary>
    public const string ReusedEditorialParts = "reusedEditorialParts";

    public const char ReusedPartsSeparator = ',';

    /// <summary>What every body part's field name starts with; the part's position follows it.</summary>
    public const string BodyPartPrefix = "body.";

    /// <summary>The field name of one part of the body, by its position counting from 0.</summary>
    public static string BodyPart(int index) =>
        BodyPartPrefix + index.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The body as the parts it is stored in, each within one row, in order.
    /// </summary>
    /// <remarks>
    /// Cut by length and never between the two halves of a surrogate pair. Where a cut falls says nothing about
    /// the text: the parts are only ever read back joined.
    /// </remarks>
    public static IReadOnlyList<string> Split(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var parts = new List<string>();

        for (var start = 0; start < body.Length;)
        {
            var length = Math.Min(AiPolicy.ChangeValueMaxLength, body.Length - start);

            if (start + length < body.Length && char.IsHighSurrogate(body[start + length - 1]))
            {
                length--;
            }

            parts.Add(body.Substring(start, length));
            start += length;
        }

        return parts;
    }

    /// <summary>The body, from its stored parts: the rows named by <see cref="BodyPart"/>, in their order.</summary>
    public static string Join(IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var body = new StringBuilder();

        foreach (var part in fields
                     .Where(field => field.Key.StartsWith(BodyPartPrefix, StringComparison.Ordinal))
                     .OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            body.Append(part.Value);
        }

        return body.ToString();
    }
}

/// <summary>The keys an LF.3.1 request travels under in <c>AiOperation.TaskInputsJson</c>.</summary>
/// <remarks>
/// No workspace and no creative context: the draft is found under the workspace filter, and the piece of work
/// it belongs to is read from the draft, not taken from the request.
/// </remarks>
public static class LongFormSectionInputs
{
    public const string LongFormDraftId = "longFormDraftId";

    /// <summary>The revision of the draft the request was composed against.</summary>
    public const string RevisionNumber = "revisionNumber";

    public const string SectionId = "sectionId";

    /// <summary>
    /// What the creator wants changed, in their own words, when a section is being written again. Absent on a
    /// first write.
    /// </summary>
    /// <remarks>
    /// Creator text, and so never rendered into the task: it reaches a model fenced as untrusted, beside the
    /// body it is about.
    /// </remarks>
    public const string Instruction = "instruction";

    public static Guid? ReadDraftId(IReadOnlyDictionary<string, string>? inputs) => ReadGuid(inputs, LongFormDraftId);

    public static Guid? ReadSectionId(IReadOnlyDictionary<string, string>? inputs) => ReadGuid(inputs, SectionId);

    /// <summary>The revision number, or null when it is absent or is not a whole number of one or more.</summary>
    public static int? ReadRevisionNumber(IReadOnlyDictionary<string, string>? inputs) =>
        PhotographyConceptInputs.Read(inputs, RevisionNumber) is { } value
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number >= 1
            ? number
            : null;

    /// <summary>The creator's instruction, or null when there is none.</summary>
    public static string? ReadInstruction(IReadOnlyDictionary<string, string>? inputs) =>
        PhotographyConceptInputs.Read(inputs, Instruction) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static Guid? ReadGuid(IReadOnlyDictionary<string, string>? inputs, string key) =>
        PhotographyConceptInputs.Read(inputs, key) is { } value && Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : null;
}
