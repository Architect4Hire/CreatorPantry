using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// PRM-005's export document: one saved prompt written as JSON, the same bytes for the same prompt every time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written field by field with an explicit writer, not serialized by reflection, and that is the whole
/// design.</strong> A reflected export grows whenever a <c>ServiceModel</c> does, so a field added for a
/// screen would arrive in files creators have already downloaded and archived, without anyone deciding it
/// should. Here every field is a line: adding one to the document is an edit to this file, which is a diff a
/// reviewer sees. The same reason <c>RecipeSnapshotDocument</c> is serialized explicitly rather than mapped.
/// </para>
/// <para>
/// <strong>What makes it deterministic.</strong> The order is this method's order rather than a type's
/// declaration order; the indentation is fixed; <c>NewLine</c> is <c>"\n"</c> so the bytes do not depend on the
/// host the API runs on; the timestamp is normalized to UTC at full stored precision; and there is <strong>no
/// <c>exportedAt</c></strong> — a prompt row is immutable, so one prompt has one export, and a clock in the
/// document would mean every download of it differed.
/// </para>
/// <para>
/// <strong>Nulls are written rather than omitted</strong>, the distinction
/// <c>RecipeSnapshotSerializer</c> records: "this prompt pins no template" and "this document predates
/// templates" must not read alike in a file meant to be opened again later.
/// </para>
/// <para>
/// <strong>It takes the detail ServiceModel rather than the row.</strong> One mapping decides what is
/// publishable about a prompt, so this document and <c>PRM-003</c>'s response cannot disagree about that — and
/// the two Guids that never leave the server (tenancy.md) are already gone before this code runs, rather than
/// being filtered here where a missed line would publish one.
/// </para>
/// </remarks>
public static class PromptRecordExportWriter
{
    /// <summary>
    /// The document's declared shape, named the way <c>AiProposal.OutputSchemaVersion</c> names its own.
    /// </summary>
    /// <remarks>
    /// It changes when a field is removed or its meaning changes, not when one is added — an added field is a
    /// compatible change (api-contract.md), and a reader that ignores unknown properties keeps working.
    /// </remarks>
    public const string SchemaVersion = "prompt.record.v1";

    public static string Write(PromptDetailServiceModel prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SchemaVersion);

            // The record under its own key, so document metadata and prompt data never share a namespace —
            // and so an author or an asset id can be added beside it later without re-versioning the shape.
            writer.WriteStartObject("prompt");
            writer.WriteString("promptRecordId", prompt.PromptRecordId);
            writer.WriteString("channelKey", prompt.ChannelKey);

            // The declared enum name, which is what the API publishes too (Program.cs's JsonStringEnumConverter),
            // so one vocabulary covers the response and the file.
            writer.WriteString("imageKind", prompt.ImageKind.ToString());
            writer.WriteString("text", prompt.Text);
            WriteText(writer, "generatedText", prompt.GeneratedText);
            WriteText(writer, "label", prompt.Label);
            writer.WriteString("source", prompt.Source.ToString());
            WriteId(writer, "aiProposalId", prompt.AiProposalId);
            WriteId(writer, "recipeId", prompt.RecipeId);
            WriteId(writer, "recipeVersionId", prompt.RecipeVersionId);
            WriteText(writer, "promptTemplateId", prompt.PromptTemplateId);
            WriteText(writer, "promptTemplateVersion", prompt.PromptTemplateVersion);
            WriteText(writer, "promptTemplateBodyChecksum", prompt.PromptTemplateBodyChecksum);
            writer.WriteString("createdAt", Timestamp(prompt.CreatedAt));
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        // No byte-order mark: GetString decodes the bytes written, and nothing prepends a preamble to them.
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteId(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } id)
        {
            writer.WriteString(name, id);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    /// <summary>
    /// The saved moment in UTC, at the precision the row stores.
    /// </summary>
    /// <remarks>
    /// <c>UtcDateTime</c> rather than the value as read, so the document does not change shape with the offset
    /// a row happened to be written with; round-trip format rather than a seconds-truncating one, because an
    /// export that quietly dropped sub-second precision would not round-trip the row it came from.
    /// </remarks>
    private static string Timestamp(DateTimeOffset savedAt) =>
        savedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
