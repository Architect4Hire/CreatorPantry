using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract AF.6.3's answer must satisfy: one post body for each channel the request named, and nothing
/// else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The model writes copy only.</strong> There is no field for a character count, a limit, a hashtag
/// list, a link, a schedule or a verdict. Every one of those is the server's to measure or the creator's to
/// decide, and a model that returned one fails strict shape validation as an unknown field.
/// </para>
/// <para>
/// <strong>No "written from" block</strong>, for the reason <see cref="AiImagePromptOutputDocument"/> records:
/// what grounded an answer is the server's fact, kept as the proposal's provenance, and a model asserting it
/// would be an unfalsifiable claim.
/// </para>
/// </remarks>
public sealed record AiChannelPostsOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>One entry per requested channel, each channel exactly once.</summary>
    public IReadOnlyList<AiChannelPost> Posts { get; init; } = [];

    /// <summary>What the creator should know about these posts, including anything that could not be honoured.</summary>
    public IReadOnlyList<AiChannelPostsOutputWarning> Warnings { get; init; } = [];
}

/// <summary>One channel's post.</summary>
public sealed record AiChannelPost
{
    /// <summary>The channel this body is for, exactly as the task named it.</summary>
    public required string ChannelKey { get; init; }

    /// <summary>The post, as plain text a creator edits before accepting.</summary>
    public required string Body { get; init; }
}

/// <summary>One thing to tell the creator about the posts.</summary>
public sealed record AiChannelPostsOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    /// <summary>The channel it is about, or null when it is about all of them.</summary>
    public string? ChannelKey { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// The JSON Schema a model is shown for AF.6.3, generated from <see cref="AiChannelPostsOutputDocument"/>
/// rather than hand-written, so the shape asked for and the shape judged cannot drift apart.
/// </summary>
public static class AiChannelPostsOutputSchema
{
    private static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly Lazy<string> Exported = new(() =>
        SchemaJson.GetJsonSchemaAsNode(typeof(AiChannelPostsOutputDocument)).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The schema, as indented JSON suitable for embedding in a prompt.</summary>
    public static string Json => Exported.Value;
}

/// <summary>The field names an AF.6.3 post's rows are stored under.</summary>
/// <remarks>
/// Stable, because a stored proposal's rows are read back by whatever turns them into post revisions (AF.6.4).
/// </remarks>
public static class ChannelPostFields
{
    public const string ChannelKey = "channelKey";

    /// <summary>The body's length as the channel's profile counts it.</summary>
    public const string CharacterCount = "characterCount";

    public const string CharacterLimit = "characterLimit";

    /// <summary><c>Within</c> or <c>Over</c>.</summary>
    public const string LimitStatus = "limitStatus";

    /// <summary>The profile version that measured it.</summary>
    public const string ProfileVersion = "profileVersion";

    public const string Within = "Within";

    public const string Over = "Over";
}

/// <summary>The keys an AF.6.3 request travels under in <c>AiOperation.TaskInputsJson</c>.</summary>
public static class ChannelPostsInputs
{
    public const string CreativeContextId = "creativeContextId";

    /// <summary>The requested channel keys, joined by <see cref="PhotographyConceptInputs.ListSeparator"/>.</summary>
    public const string ChannelKeys = "channelKeys";

    public static Guid? ReadContextId(IReadOnlyDictionary<string, string>? inputs) =>
        PhotographyConceptInputs.Read(inputs, CreativeContextId) is { } value && Guid.TryParse(value, out var id)
            ? id
            : null;

    public static IReadOnlyList<string> ReadChannelKeys(IReadOnlyDictionary<string, string>? inputs) =>
        PhotographyConceptInputs.ReadList(inputs, ChannelKeys);
}
