using CreatorPantry.Domain.Managers.Patching;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The body of <c>PATCH /api/v1/workspaces/{workspaceSlug}/dam-assets/{assetId}</c> (DAM-004): a creator
/// correcting what they said about an asset they already have.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Submitted-field semantics.</strong> A field the body does not mention is left exactly as it is. A
/// field mentioned with a value is set to it. A field mentioned as <c>null</c> is cleared. The three are
/// distinguishable because every field is a <see cref="PatchField{T}"/> — see that type for how the serializer
/// tells absence from <c>null</c>, and why a nullable property alone cannot. Without the distinction a client
/// that omits a field it has never heard of silently blanks it, which here means losing a creator's alt text or
/// their attribution.
/// </para>
/// <para>
/// <strong>Tags replace, they do not merge.</strong> A submitted list becomes the asset's complete set; an empty
/// list and an explicit <c>null</c> both clear every tag. The same choice <c>UpdateRecipeViewModel</c> makes, and
/// for the same reason: merging would leave no way to remove one, and an add/remove pair would be two ways to say
/// one thing.
/// </para>
/// <para>
/// <strong>Nothing about the bytes is here, and that is the whole shape of this contract.</strong> No object key,
/// no version number, no media type, no dimensions, no checksum. Those are facts about stored bytes that the
/// server established by reading them, and a request that could state them could state them wrongly — the same
/// argument <see cref="MediaAssetMetadataInput"/> makes about creation. A new file is a new version (DAM-010),
/// never an edit.
/// </para>
/// <para>
/// <strong><c>Kind</c> is absent deliberately.</strong> It records where the bytes came from — an upload, an
/// import, a derivative, a model — which is lineage rather than classification, and this prompt's restriction is
/// that a metadata patch never replaces lineage. Letting a creator relabel an <c>AiGenerated</c> asset as
/// <c>Original</c> would erase the only column that remembers a model was involved, which ai.md requires be
/// recorded. The editorial classification a creator <em>may</em> correct is
/// <see cref="CuisineId"/>, <see cref="CourseId"/>, <see cref="ChannelKey"/>, <see cref="PlatformKey"/>,
/// <see cref="StyleKey"/>, <see cref="Day"/> and <see cref="Tags"/>.
/// </para>
/// <para>
/// <strong>Also absent:</strong> no <c>WorkspaceId</c> or owner — ownership is never request input (tenancy.md);
/// no id, because the route names the asset; no <c>deletedAt</c>, because deleting is DAM-005 and a patch is not
/// a way to delete or restore; no audit fields, because the server sets them from the resolved context; and no
/// recipe or prompt lineage, because a link is its own resource and an immutable prompt record cannot be edited
/// at all.
/// </para>
/// <para>
/// <strong>Every field is nullable inside its <see cref="PatchField{T}"/>, including <see cref="Title"/>.</strong>
/// The wire can send <c>null</c> for anything, so the type says what the wire can carry and the validator says
/// what is allowed — which is where a refusal can name the field and explain itself. Clearing a title is refused
/// there, not made unrepresentable here.
/// </para>
/// </remarks>
public sealed record MediaAssetMetadataPatchViewModel
{
    /// <summary>
    /// The <c>concurrencyToken</c> from the asset this edit was composed against. Required.
    /// </summary>
    /// <remarks>
    /// Round-tripped from the read, which publishes it as
    /// <see cref="MediaAssetDetailServiceModel.ConcurrencyToken"/>. It is what makes a second creator's edit a
    /// recoverable conflict instead of a silent overwrite of the first one's work, so there is no default and no
    /// way to opt out: a write with nothing to check against is last-write-wins by another name. Opaque — a
    /// client stores it and sends it back, and never parses or compares it.
    /// </remarks>
    public string? ExpectedConcurrencyToken { get; init; }

    /// <summary>The creator's own name for the asset. May be changed, never cleared.</summary>
    public PatchField<string?> Title { get; init; }

    public PatchField<string?> Description { get; init; }

    /// <summary>
    /// What the image shows, for anyone who cannot see it. Only ever the creator's own words.
    /// </summary>
    /// <remarks>
    /// Nothing in this codebase writes alt text from a filename, a recipe title or a prompt, and a patch does not
    /// either — that would be describing pixels nothing has analysed (media.md, ai.md). Clearable, because a
    /// creator who decides their own description was wrong should be able to remove it rather than being forced
    /// to leave something inaccurate in place.
    /// </remarks>
    public PatchField<string?> AltText { get; init; }

    /// <summary>The channel this asset was made for. Opaque workspace vocabulary.</summary>
    public PatchField<string?> ChannelKey { get; init; }

    /// <inheritdoc cref="ChannelKey"/>
    public PatchField<string?> PlatformKey { get; init; }

    /// <summary>The weekly-theme day it belongs to.</summary>
    public PatchField<DayOfWeek?> Day { get; init; }

    /// <inheritdoc cref="ChannelKey"/>
    public PatchField<string?> StyleKey { get; init; }

    /// <summary>Platform reference vocabulary, shared and not workspace-owned.</summary>
    public PatchField<Guid?> CuisineId { get; init; }

    /// <inheritdoc cref="CuisineId"/>
    public PatchField<Guid?> CourseId { get; init; }

    /// <summary>Who holds the rights. Clearable: a creator correcting their own record may remove it.</summary>
    public PatchField<string?> RightsHolder { get; init; }

    /// <inheritdoc cref="RightsHolder"/>
    public PatchField<string?> AttributionText { get; init; }

    /// <summary>
    /// The asset's complete set of tags, from the workspace's own vocabulary. Replaces rather than merges.
    /// </summary>
    /// <remarks>
    /// Unknown ids are refused, never created here — a tag is workspace vocabulary with its own write path, and a
    /// patch that invented one would let a typo become a permanent entry in the creator's own taxonomy.
    /// </remarks>
    public PatchField<IReadOnlyList<Guid>?> Tags { get; init; }

    /// <summary>
    /// What this patch asked for, as a shape the idempotency fingerprint can serialize.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The view model itself cannot be fingerprinted.</strong>
    /// <c>PatchFieldJsonConverter.Write</c> throws by design — a converter cannot omit a property, so it could
    /// only spell "absent" as some value that is really a different request. Passing this record to
    /// <c>IdempotentCommand.Fingerprint</c> would therefore throw where the create path happily passes its whole
    /// payload.
    /// </para>
    /// <para>
    /// <strong>A submitted field becomes a key; an absent field is simply missing.</strong> That is what keeps the
    /// three states apart through serialization: <c>{"title": null}</c> is a request to clear, and no <c>title</c>
    /// key at all is a request to leave it alone. A flat shape with nulls for both would make them one fingerprint
    /// and two different edits.
    /// </para>
    /// <para>
    /// The token is included because it is part of what the caller asked for — two edits of the same fields
    /// composed against different reads are different requests.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Fingerprint()
    {
        var submitted = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["expectedConcurrencyToken"] = ExpectedConcurrencyToken,
        };

        Add(submitted, "title", Title);
        Add(submitted, "description", Description);
        Add(submitted, "altText", AltText);
        Add(submitted, "channelKey", ChannelKey);
        Add(submitted, "platformKey", PlatformKey);
        Add(submitted, "styleKey", StyleKey);
        Add(submitted, "rightsHolder", RightsHolder);
        Add(submitted, "attributionText", AttributionText);
        Add(submitted, "day", Day);
        Add(submitted, "cuisineId", CuisineId);
        Add(submitted, "courseId", CourseId);

        if (Tags.IsSubmitted)
        {
            // Ordered, so re-sending the same tags in another order is the same request rather than a false
            // mismatch — which is what the merge already treats it as.
            submitted["tags"] = Tags.Value?
                .Select(tag => tag.ToString("D"))
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        return submitted;
    }

    private static void Add<T>(Dictionary<string, object?> into, string name, PatchField<T> field)
    {
        if (field.IsSubmitted)
        {
            into[name] = field.Value;
        }
    }
}
