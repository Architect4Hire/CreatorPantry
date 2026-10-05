using System.Globalization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a creator chose about brand context when they asked for a generation, as it travels in
/// <c>AiOperation.TaskInputsJson</c> and comes back to the handler (11A.20).
/// </summary>
/// <remarks>
/// <para>
/// <strong>References, not values.</strong> The seam this replaces pinned brand facts by value — brand name,
/// audience and locale copied into the inputs at request time. A reference is pinned instead, because a guide
/// version is immutable content the assembler can re-read identically while the facts it would have copied are
/// a small fraction of what the package now carries, and copying all of it into an input dictionary with a
/// 12,000-character ceiling is not a thing to attempt.
/// </para>
/// <para>
/// <strong>The absence of a key is a decision, not a gap.</strong> An operation queued before this seam existed
/// carries none of these, and reads back as "use brand voice, no pinned guide" — which is the default a creator
/// who never saw the control would have got. Nothing re-interprets an old request as an opt-out.
/// </para>
/// <para>
/// <strong>No workspace, and the ids here are all client-supplied.</strong> There is deliberately no workspace
/// key: the worker resolves it. The guide and document ids come from the creator, and they are safe to carry
/// because the assembler resolves every one of them through a brand facade under the workspace query filter — a
/// neighbour's guide id reads back as "does not exist" and a neighbour's document as unavailable, neither of
/// which discloses that it is somebody else's.
/// </para>
/// </remarks>
public static class BrandContextRequestInputs
{
    /// <summary>"false" turns brand voice off for this generation. Any other value, or absence, leaves it on.</summary>
    public const string UseBrandVoice = "useBrandVoice";

    public const string BrandGuideId = "brandGuideId";

    public const string BrandGuideVersionNumber = "brandGuideVersionNumber";

    /// <summary>
    /// The audience for this piece, overriding the brand profile default.
    /// </summary>
    /// <remarks>
    /// Named <c>brandAudience</c> rather than <c>audience</c> on purpose: the editorial and SEO seams used
    /// <c>audience</c> for the profile default they pinned by value, and a key that means one thing in an old
    /// queued operation and another in a new one is the kind of overlap that is invisible until a replay.
    /// </remarks>
    public const string Audience = "brandAudience";

    /// <summary>The documents to ground on, comma-separated. Absent means the assembler ranks and picks.</summary>
    public const string SourceDocumentIds = "brandSourceDocumentIds";

    /// <summary>
    /// Writes a creator's brand-context choices into an operation's inputs, omitting every default.
    /// </summary>
    /// <remarks>
    /// Defaults are omitted rather than written explicitly, so the stored inputs say what the creator chose and
    /// not what the product happened to default to on the day they asked.
    /// </remarks>
    public static void Write(IDictionary<string, string> values, BrandContextRequestSelection selection)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(selection);

        if (!selection.UseBrandVoice)
        {
            // The only non-default this records, and it records nothing else: a generation with brand voice off
            // has no guide, no audience and no documents to name.
            values[UseBrandVoice] = "false";

            return;
        }

        if (selection.Guide is { } guide)
        {
            values[BrandGuideId] = guide.GuideId.ToString();
            values[BrandGuideVersionNumber] = guide.VersionNumber.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(selection.Audience))
        {
            values[Audience] = selection.Audience;
        }

        if (selection.SourceDocumentIds.Count > 0)
        {
            values[SourceDocumentIds] = string.Join(',', selection.SourceDocumentIds);
        }
    }

    /// <summary>
    /// Reads those choices back, or null when the inputs cannot be understood.
    /// </summary>
    /// <remarks>
    /// Null means a value this server wrote cannot be parsed, which is a defect rather than bad creator input —
    /// the edge validated the request long before it reached here. A handler turns it into a validation failure
    /// rather than generating without the context the creator asked for, matching how every other handler treats
    /// unreadable inputs.
    /// </remarks>
    public static BrandContextRequestSelection? Read(IReadOnlyDictionary<string, string>? inputs)
    {
        if (inputs is null)
        {
            return BrandContextRequestSelection.Default;
        }

        if (inputs.TryGetValue(UseBrandVoice, out var useBrandVoice)
            && string.Equals(useBrandVoice, "false", StringComparison.OrdinalIgnoreCase))
        {
            return BrandContextRequestSelection.Off;
        }

        var hasGuideId = inputs.TryGetValue(BrandGuideId, out var rawGuideId);
        var hasVersion = inputs.TryGetValue(BrandGuideVersionNumber, out var rawVersion);

        if (hasGuideId != hasVersion)
        {
            // Half a selection. Refused rather than resolved to the active guide, which would silently hand back
            // a version the creator did not ask for.
            return null;
        }

        BrandGuideSelection? guide = null;

        if (hasGuideId)
        {
            if (!Guid.TryParse(rawGuideId, out var guideId)
                || guideId == Guid.Empty
                || !int.TryParse(rawVersion, CultureInfo.InvariantCulture, out var versionNumber)
                || versionNumber <= 0)
            {
                return null;
            }

            guide = new BrandGuideSelection(guideId, versionNumber);
        }

        var documents = new List<Guid>();

        if (inputs.TryGetValue(SourceDocumentIds, out var rawDocuments))
        {
            foreach (var part in rawDocuments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var documentId) || documentId == Guid.Empty)
                {
                    return null;
                }

                documents.Add(documentId);
            }
        }

        var audience = inputs.GetValueOrDefault(Audience);

        return new BrandContextRequestSelection(
            UseBrandVoice: true,
            guide,
            string.IsNullOrWhiteSpace(audience) ? null : audience,
            documents);
    }
}

/// <summary>
/// One generation's brand-context choices, independent of which capability asked.
/// </summary>
/// <param name="UseBrandVoice">
/// False is the creator asking for no brand context at all — not an empty one. A handler then passes no package to
/// the assembler and records no provenance row, which is how "I turned it off" stays distinguishable from "it had
/// nothing to give".
/// </param>
/// <param name="Guide">
/// A pinned guide and version number, or null for the workspace's active version. A version number rather than a
/// version id, for the reason <see cref="BrandGuideSelection"/> gives: the number is what a creator sees, and
/// resolving it inside the named guide is what stops a caller naming a row by an id it should not have guessed.
/// </param>
/// <param name="Audience">This piece's audience, or null for the brand profile default.</param>
/// <param name="SourceDocumentIds">
/// Documents to ground on, or empty to let the assembler rank and pick. Empty is not "none": a creator who names
/// nothing gets the server's relevance guess, capped lower than a named list on purpose.
/// </param>
public sealed record BrandContextRequestSelection(
    bool UseBrandVoice,
    BrandGuideSelection? Guide,
    string? Audience,
    IReadOnlyList<Guid> SourceDocumentIds)
{
    /// <summary>Brand voice on, nothing pinned — what a request that says nothing means.</summary>
    public static BrandContextRequestSelection Default { get; } = new(true, null, null, []);

    /// <summary>Brand voice off.</summary>
    public static BrandContextRequestSelection Off { get; } = new(false, null, null, []);

    /// <summary>
    /// The assembler request for one task, or null when the creator asked for no brand context.
    /// </summary>
    /// <remarks>
    /// The channel is null on this overload. No <em>writing</em> capability built so far targets a channel — the
    /// channel catalogue holds social channels, and a blog package, SEO metadata, a recipe concept and a first
    /// draft are none of them — so a channel on those requests could only pull a social variant over the
    /// long-form guidance they need.
    /// <para>
    /// IMG-001 is the first capability that does target one, and it calls
    /// <see cref="ToRequest(AiTaskType, string?)"/>: a photograph is planned for a place it will be seen, so the
    /// channel is what selects the visual guidance the creator wrote for that place rather than their visual
    /// identity in general.
    /// </para>
    /// </remarks>
    public BrandContextRequest? ToRequest(AiTaskType taskType) => ToRequest(taskType, channelKey: null);

    /// <summary>
    /// The assembler request for one task against one channel, or null when the creator asked for no brand
    /// context.
    /// </summary>
    /// <param name="channelKey">
    /// The channel this piece is for, or null for the workspace's guidance in general. Validated against the
    /// catalogue before it reaches here — the assembler answers
    /// <see cref="BrandContextErrors.ChannelInvalid"/> for a key the product does not know, so an unknown
    /// channel is refused rather than silently ignored.
    /// </param>
    public BrandContextRequest? ToRequest(AiTaskType taskType, string? channelKey) => UseBrandVoice
        ? new BrandContextRequest(taskType, channelKey, Audience, Guide, SourceDocumentIds)
        : null;
}
