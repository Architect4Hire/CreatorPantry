using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The boundary AF.6.3's answer crosses to become an <see cref="AiChannelPostsOutputDocument"/>, mirroring
/// <see cref="AiImagePromptOutputValidator"/>'s stages and its two guarantees — the payload appears on neither
/// the success nor the failure path, and nothing is repaired.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it refuses is what makes an answer unusable as a whole</strong>: a channel nobody asked for, a
/// requested channel left out or written twice, a body that is blank, cannot be stored, or carries HTML or a
/// code fence, and a warning that is not a warning. A post for a channel the request did not name is never
/// dropped to rescue the rest — that would be repairing the answer.
/// </para>
/// <para>
/// <strong>What it deliberately does not refuse, and why each is a decision rather than a gap.</strong> A body
/// over its channel's limit, too many hashtags and a link where the channel carries it elsewhere are measured
/// afterwards by the channel's writing profile and returned flagged, never trimmed (this capability's
/// RESTRICTION). A safety, allergen, dietary or health claim, an invented figure and an invented search
/// metric are found afterwards by the claim scanners and reported beside the post, for the creator to judge:
/// those words name dishes as often as they assert anything — "Gluten-Free Brownies" is a recipe title — and
/// refusing here would cost the creator every channel of a generation over one phrase the single corrective
/// re-ask cannot reliably fix.
/// </para>
/// <para>
/// <strong>What the prompt asks for and no code enforces</strong>: that a post is about the piece of work it
/// was given, that it says nothing about a picture beyond its supplied description, and that it ignores an
/// instruction planted in a title, a brief, a recipe line or a brand passage. Each is a question about the
/// model's behaviour, examined by review of real output and not asserted here.
/// </para>
/// </remarks>
public static class AiChannelPostsOutputValidator
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // So "posts": null is refused rather than crashing the rules below; see AiImagePromptOutputValidator.
        RespectNullableAnnotations = true,
    };

    // The two invisible code points an emoji is built with. Each is allowed only where an emoji needs it.
    private const int ZeroWidthJoiner = 0x200D;

    private const int EmojiVariationSelector = 0xFE0F;

    private const int KeycapMark = 0x20E3;

    /// <summary>
    /// What marks a finding as the server's, or a warning as the model's. A model warning that carries one
    /// would read, beside the real ones, as the server vouching for it.
    /// </summary>
    private static readonly string[] ServerFindingPrefixes =
        ["[editorial.", "[seo.", "[posts.", "[brand.", "[brand_context.", "[model]"];

    private static readonly Regex Markup = new(
        @"<[A-Za-z/!][^>]*>|```",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Validates one model answer against the schema version its template declared.</summary>
    /// <param name="requestedChannels">The channel keys the request named. An answer is held to exactly these.</param>
    public static AiOutputValidationOutcome<AiChannelPostsOutputDocument> Validate(
        string? payload, string expectedSchemaVersion, IReadOnlyCollection<string> requestedChannels)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedSchemaVersion);
        ArgumentNullException.ThrowIfNull(requestedChannels);

        return Envelope(payload)
            ?? SchemaVersion(payload!, expectedSchemaVersion)
            ?? Shape(payload!, out var document)
            ?? Domain(document!, requestedChannels)
            ?? AiOutputValidationOutcome<AiChannelPostsOutputDocument>.Success(document!);
    }

    /// <summary>The adapter <see cref="Gateways.IAiCompletionGateway"/> calls, bound to one request's channels.</summary>
    public static AiOutputValidatorDelegate<AiChannelPostsOutputDocument> For(IReadOnlyCollection<string> requestedChannels)
    {
        ArgumentNullException.ThrowIfNull(requestedChannels);

        return (payload, expectedSchemaVersion, _) => Validate(payload, expectedSchemaVersion, requestedChannels);
    }

    private static AiOutputValidationOutcome<AiChannelPostsOutputDocument>? Envelope(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Reject(AiOutputReason.EmptyPayload, "The provider returned no content.", correctable: true);
        }

        return Encoding.UTF8.GetByteCount(payload) > AiPolicy.OutputPayloadMaxBytes
            ? Reject(
                AiOutputReason.PayloadTooLarge,
                $"The response exceeded {AiPolicy.OutputPayloadMaxBytes} bytes and was not parsed.",
                correctable: false)
            : null;
    }

    private static AiOutputValidationOutcome<AiChannelPostsOutputDocument>? SchemaVersion(
        string payload, string expected)
    {
        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            return Reject(
                AiOutputReason.MalformedJson,
                $"The response is not valid JSON ({Sanitize(exception.Message)}).",
                correctable: true);
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind is not JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind is not JsonValueKind.String)
            {
                return Reject(
                    AiOutputReason.SchemaVersionMissing,
                    "The response does not declare a string 'schemaVersion'.",
                    correctable: true);
            }

            if (!string.Equals(version.GetString(), expected, StringComparison.Ordinal))
            {
                return Reject(
                    AiOutputReason.SchemaVersionMismatch,
                    $"The response declares schema '{Sanitize(version.GetString())}' but '{expected}' was "
                        + "required.",
                    correctable: true);
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiChannelPostsOutputDocument>? Shape(
        string payload, out AiChannelPostsOutputDocument? document)
    {
        document = null;

        try
        {
            document = JsonSerializer.Deserialize<AiChannelPostsOutputDocument>(payload, OutputJson);
        }
        catch (JsonException exception)
        {
            // An unknown member is how a hashtag list, a character count or a schedule arrives here — refused
            // rather than dropped, so nothing the server is meant to measure is taken from the model instead.
            var unknownMember = exception.Message.Contains("could not be mapped", StringComparison.Ordinal);

            return Reject(
                unknownMember ? AiOutputReason.UnknownField : AiOutputReason.ShapeInvalid,
                $"The response does not match the required schema ({Sanitize(exception.Message)}).",
                correctable: true);
        }

        return document is null
            ? Reject(AiOutputReason.ShapeInvalid, "The response deserialized to nothing.", correctable: true)
            : null;
    }

    /// <summary>The rules the type system cannot state.</summary>
    private static AiOutputValidationOutcome<AiChannelPostsOutputDocument>? Domain(
        AiChannelPostsOutputDocument document, IReadOnlyCollection<string> requestedChannels)
    {
        var requested = requestedChannels.ToHashSet(StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (var post in document.Posts)
        {
            // Exact, ordinal: the task named each key, and "Instagram" is not the channel "instagram".
            if (!requested.Contains(post.ChannelKey))
            {
                // The key is the model's own text, so it is not echoed: a failure message is stored, and is
                // read back to the model in the corrective turn.
                return Reject(
                    AiOutputReason.ChannelPostsChannelNotRequested,
                    "The response wrote a post for a channel the request did not name.",
                    correctable: true);
            }

            if (!written.Add(post.ChannelKey))
            {
                return Reject(
                    AiOutputReason.ChannelPostsChannelRepeated,
                    $"The response wrote more than one post for '{post.ChannelKey}'.",
                    correctable: true);
            }

            if (Body(post) is { } failure)
            {
                return failure;
            }
        }

        // Ordered, so the same answer is always refused for the same channel.
        if (requested.Except(written).Order(StringComparer.Ordinal).FirstOrDefault() is { } missing)
        {
            return Reject(
                AiOutputReason.ChannelPostsChannelMissing,
                $"The response wrote no post for '{missing}', which the request named.",
                correctable: true);
        }

        if (document.Warnings.Count > AiPolicy.MaxChannelPostsWarnings)
        {
            return Reject(
                AiOutputReason.ChannelPostsWarningInvalid,
                $"The response carries more than {AiPolicy.MaxChannelPostsWarnings} warnings.",
                correctable: true);
        }

        foreach (var warning in document.Warnings)
        {
            // Capped at the column's length: a longer warning would pass here and fail the write after the
            // provider had been paid.
            // The handler stores a model warning under AiPolicy.ModelWarningLabel, so the room for it is the
            // column's less the label's.
            if (warning.Kind is AiWarningKind.Unspecified
                || !Enum.IsDefined(warning.Kind)
                || string.IsNullOrWhiteSpace(warning.Message)
                || warning.Message.Length > AiPolicy.MessageMaxLength - AiPolicy.ModelWarningLabel.Length
                || (warning.ChannelKey is not null && !requested.Contains(warning.ChannelKey)))
            {
                return Reject(
                    AiOutputReason.ChannelPostsWarningInvalid,
                    "A warning declares its kind, is a sentence that fits the space a warning has, and names a "
                        + "requested channel or none.",
                    correctable: true);
            }

            if (ServerFindingPrefixes.Any(prefix => warning.Message.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return Reject(
                    AiOutputReason.ChannelPostsWarningInvalid,
                    "A warning imitates a server finding; write the caution in your own words.",
                    correctable: true);
            }

            if (Characters(warning.Message, "a warning message") is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    private static AiOutputValidationOutcome<AiChannelPostsOutputDocument>? Body(AiChannelPost post)
    {
        if (string.IsNullOrWhiteSpace(post.Body))
        {
            return Reject(
                AiOutputReason.ChannelPostsBodyInvalid,
                $"The post for '{post.ChannelKey}' is blank.",
                correctable: true);
        }

        // The storage ceiling, not the channel's limit. A body over its channel's limit is kept and flagged.
        if (post.Body.Length > AiPolicy.ChannelPostBodyMaxLength)
        {
            return Reject(
                AiOutputReason.ChannelPostsBodyInvalid,
                $"The post for '{post.ChannelKey}' is {post.Body.Length} characters; at most "
                    + $"{AiPolicy.ChannelPostBodyMaxLength} can be stored.",
                correctable: true);
        }

        if (Markup.IsMatch(post.Body))
        {
            return Reject(
                AiOutputReason.ChannelPostsBodyInvalid,
                $"The post for '{post.ChannelKey}' carries HTML or a code fence. A post is plain text.",
                correctable: true);
        }

        return Characters(post.Body, $"the post for '{post.ChannelKey}'");
    }

    /// <summary>
    /// Refuses a control character and any invisible one that is not holding an emoji together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by code point, not by UTF-16 unit: the Unicode tag characters that can carry a whole hidden
    /// sentence live above the BMP, where a per-<c>char</c> test sees only surrogates.
    /// </para>
    /// <para>
    /// <strong>Why this is a refusal when a claim is only flagged.</strong> An invisible character between two
    /// letters has no honest use in a post, and it is exactly what hides "gluten-free" from a phrase list
    /// while a reader still sees it — so the scanners that run afterwards are only as good as this check. The
    /// joiner and the emoji variation selector are allowed where an emoji needs them and nowhere else: after
    /// a symbol, never after a letter, a digit or punctuation, so neither can split a word, a hashtag, a
    /// mention or a web address.
    /// </para>
    /// </remarks>
    private static AiOutputValidationOutcome<AiChannelPostsOutputDocument>? Characters(string value, string what)
    {
        var runes = value.EnumerateRunes().ToArray();

        for (var index = 0; index < runes.Length; index++)
        {
            var rune = runes[index];
            var category = Rune.GetUnicodeCategory(rune);

            if (category is UnicodeCategory.Control && rune.Value is not ('\t' or '\n' or '\r'))
            {
                return Reject(
                    AiOutputReason.IllegalCharacters,
                    $"The response contains {what} with a control character.",
                    correctable: true);
            }

            if (IsInvisible(rune, category) && !HoldsAnEmojiTogether(runes, index))
            {
                return Reject(
                    AiOutputReason.IllegalCharacters,
                    $"The response contains {what} with an invisible formatting character.",
                    correctable: true);
            }
        }

        return null;
    }

    /// <summary>Format characters, variation selectors, and the fillers and blanks that render as nothing.</summary>
    private static bool IsInvisible(Rune rune, UnicodeCategory category) =>
        category is UnicodeCategory.Format
        || rune.Value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF)
        || rune.Value is 0x034F or 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800;

    private static bool HoldsAnEmojiTogether(Rune[] runes, int index)
    {
        if (index == 0 || runes[index].Value is not (ZeroWidthJoiner or EmojiVariationSelector))
        {
            return false;
        }

        var before = runes[index - 1];

        // After a pictograph or a skin tone, or a joiner after a selector: the inside of an emoji sequence.
        if (Rune.GetUnicodeCategory(before) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol
            || (runes[index].Value is ZeroWidthJoiner && before.Value is EmojiVariationSelector))
        {
            return true;
        }

        // A keycap: a digit, '#' or '*', the selector, then the enclosing mark. The selector is allowed after
        // a digit only when the mark really follows, so it cannot be used to split a number.
        return runes[index].Value is EmojiVariationSelector
            && before.Value is (>= '0' and <= '9') or '#' or '*'
            && index + 1 < runes.Length
            && runes[index + 1].Value is KeycapMark;
    }

    private static AiOutputValidationOutcome<AiChannelPostsOutputDocument> Reject(
        string reasonCode, string message, bool correctable)
    {
        var category = reasonCode is AiOutputReason.ChannelPostsChannelNotRequested
            or AiOutputReason.ChannelPostsChannelMissing
            or AiOutputReason.ChannelPostsChannelRepeated
            or AiOutputReason.ChannelPostsBodyInvalid
            or AiOutputReason.ChannelPostsWarningInvalid
            ? AiFailureCategory.DomainInvalid
            : AiFailureCategory.OutputSchemaInvalid;

        return AiOutputValidationOutcome<AiChannelPostsOutputDocument>.Failed(
            new AiOutputFailure(category, reasonCode, Truncate(message), correctable));
    }

    private static string Sanitize(string? detail) =>
        detail is null
            ? "no detail"
            : Truncate(new string(detail.Where(character => !char.IsControl(character)).ToArray()), 160);

    private static string Truncate(string value, int maxLength = AiPolicy.DiagnosticMaxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
