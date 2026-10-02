using System.Text.Encodings.Web;
using System.Text.Json;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Renders an assembled <see cref="BrandContextPackage"/> into prompt segments (11A.20).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one place guide text becomes prompt text.</strong> 11A.20 forbids copying guide text into
/// scattered prompt files, and this is how that is held: a handler calls these two methods and adds the results
/// as reference segments. A prompt template that quotes a guide, or a handler that formats sections itself, is
/// the defect this class exists to make unnecessary.
/// </para>
/// <para>
/// <strong>Everything here is creator-owned, untrusted content, and goes in a data segment.</strong>
/// <c>PromptEnvelopeBuilder.AddReference</c> fences it with the envelope nonce and the policy segment already
/// says that fenced content is material to work from and never instructions to follow. Nothing in this renderer
/// writes an instruction of its own: an instruction placed inside a data segment would be indistinguishable from
/// one a source document had tried to forge there. The standing rule that style governs wording and never recipe
/// facts belongs in each capability's task template, which is the segment the model is told to obey.
/// </para>
/// <para>
/// <strong>No ids, no version numbers, no checksum.</strong> The model does not cite brand context and has no use
/// for its identifiers, while including them invites an answer that echoes them back as though they were content.
/// Provenance is recorded in the database by <see cref="BrandContextProvenance"/> and published through the
/// proposal detail; those are reader-facing facts, not prompt input.
/// </para>
/// </remarks>
public static class BrandContextPromptRenderer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // So a brand name reads as the creator wrote it — "Sam's Kitchen", not "Sam's Kitchen" — matching
        // what the editorial and SEO handlers already do with brand facts.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The brand facts, the selected guide sections and the structured rules, or null when the package carries
    /// none of them.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty object: a workspace with no profile and no guide should add no segment at all,
    /// because an empty one reads to a model as "the brand has no voice" rather than "none was supplied". What was
    /// missing is said to the creator instead, by <see cref="BrandContextNotices"/>.
    /// </remarks>
    public static string? Guidance(BrandContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        // The audience and the channel count towards "has something to say". A creator who named an audience for
        // this piece has told the model something it needs, and dropping it because the workspace has no profile
        // and no guide would discard the one brand fact they did supply.
        if (package.Profile is null
            && package.Guidance.Count == 0
            && package.Rules.Count == 0
            && package.Audience is null
            && package.ChannelKey is null)
        {
            return null;
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (package.Profile is { } profile)
        {
            payload["brand"] = new
            {
                name = profile.BrandName,
                about = profile.ShortDescription,
                locale = profile.Locale,
            };
        }

        // The resolved audience, wherever precedence landed it, stated once at the top level rather than inside
        // the brand block: it may have come from the request or from a guide section, and nesting it under
        // "brand" would imply the profile supplied it.
        if (package.Audience is { } audience)
        {
            payload["audience"] = audience;
        }

        if (package.ChannelKey is { } channelKey)
        {
            payload["channel"] = channelKey;
        }

        if (package.Guidance.Count > 0)
        {
            payload["voice"] = package.Guidance
                .Select(item => new
                {
                    section = item.SectionKey.ToString(),

                    // Which channel a section was written for, when it was written for one. The model is told
                    // this because general and channel-specific guidance can disagree in tone, and a reader of
                    // the rendered prompt should be able to see which it was given.
                    channel = item.ChannelKey,
                    text = item.Body,
                })
                .ToArray();
        }

        if (package.Rules.Count > 0)
        {
            payload["rules"] = package.Rules
                .Select(rule => new { kind = rule.Kind.ToString(), text = rule.Text })
                .ToArray();
        }

        return JsonSerializer.Serialize(payload, Json);
    }

    /// <summary>
    /// The cited writing samples, or null when the package carries none.
    /// </summary>
    /// <remarks>
    /// Numbered and nothing more. A writing task does not cite its samples — unlike a guide proposal, which must,
    /// and which therefore does pass passage ids — so an id here would be data the answer cannot legitimately
    /// use. The record of which passages these were lives in the provenance rows.
    /// </remarks>
    public static string? Excerpts(BrandContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.Excerpts.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            package.Excerpts
                .Select((excerpt, index) => new { sample = index + 1, text = excerpt.Text })
                .ToArray(),
            Json);
    }
}
