using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract a shorter image prompt must satisfy (AP.3.2): the creator's prompt said in fewer characters, and
/// what it no longer says.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No avoid list, and that is the guarantee rather than an omission.</strong> The creator's avoid list is
/// never shown to the model that shortens their prompt: the handler carries it into the proposal word for word,
/// so no answer can drop or reword it. A field for one here would be a second copy for a model to get wrong.
/// </para>
/// <para>
/// <strong><see cref="LeftOut"/> is the model's own account</strong>, and is shown as that: a list a creator reads
/// beside the shorter prompt, not a fact the server checked. Whether it is honest is the question the live check
/// for this capability measures, by counting detail that went missing without being listed.
/// </para>
/// </remarks>
public sealed record AiImagePromptCompactionOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>The shorter prompt, within the budget the request was given.</summary>
    public required string Prompt { get; init; }

    /// <summary>Each piece of the picture the shorter prompt no longer asks for, as a short phrase.</summary>
    public IReadOnlyList<string> LeftOut { get; init; } = [];

    /// <summary>Anything else the creator should know about the shorter prompt.</summary>
    public IReadOnlyList<AiImagePromptOutputWarning> Warnings { get; init; } = [];
}

/// <summary>
/// The JSON Schema a model is shown for a shorter prompt, generated from
/// <see cref="AiImagePromptCompactionOutputDocument"/> so the shape asked for and the shape judged cannot drift.
/// </summary>
public static class AiImagePromptCompactionOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiImagePromptCompactionOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}
