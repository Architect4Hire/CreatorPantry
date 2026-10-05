namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// A prompt to save to the creator's library, with everything it was produced from.
/// </summary>
/// <remarks>
/// <para>
/// Carries no workspace and no author: both come from the route and the caller's verified membership
/// (tenancy.md). It carries no id either — the row is immutable, so a client-chosen id would be a client
/// choosing what a permanent record is called.
/// </para>
/// <para>
/// <strong>There is no <c>generatedImageId</c> and no <c>damAssetId</c>, and their absence is the design.</strong>
/// Those two columns have no foreign key — <c>GeneratedImage</c> arrives in 12.6 and <c>DamAsset</c> in 12.9 —
/// and 12.3's carried-forward rule is that this seam must resolve them through their owning facade, inside the
/// resolved workspace, before inserting. There is no owning facade to resolve them through yet, nothing in the
/// schema can check them, and <see cref="Data.Entities.PromptRecord"/> is immutable, so a value written wrongly
/// could never be corrected — only erased, and only after 12.6's composite foreign key had already failed to
/// apply. Accepting an id this server cannot verify is therefore the one thing this request must not do. Each
/// field arrives with the prompt that adds its table, its facade check and its foreign key together, which is an
/// additive change to this shape rather than a breaking one.
/// </para>
/// <para>
/// Every field is nullable so a missing one is distinguishable from a default — <see cref="ImageKind"/> and
/// <see cref="Source"/> in particular, whose defaults (<c>Hero</c>, <c>Manual</c>) are real values an omission
/// would otherwise silently become.
/// </para>
/// </remarks>
public sealed record SavePromptRecordViewModel
{
    /// <summary>A <c>ContentChannel.Key</c>, such as <c>instagram</c>. Must be a channel still offered.</summary>
    public string? ChannelKey { get; init; }

    /// <summary>What the image is for. The requirements' "content type", and editorial rather than a media type.</summary>
    public PromptImageKind? ImageKind { get; init; }

    /// <summary>
    /// The prompt as it was actually used, after any edit the creator made. Authoritative, and required.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// The model's draft, when a model wrote one. Required for a generated prompt and refused for a manual one.
    /// </summary>
    /// <remarks>
    /// Sent beside <see cref="Text"/> rather than replaced by it, so the library can answer "what did the creator
    /// change" for as long as the row exists. Send it unchanged when the creator accepted the draft as written.
    /// </remarks>
    public string? GeneratedText { get; init; }

    /// <summary>The creator's own short name for this prompt. Optional, and theirs to word.</summary>
    public string? Label { get; init; }

    /// <summary>How the prompt came to exist. Decides which of the fields below are required.</summary>
    public PromptRecordSource? Source { get; init; }

    /// <summary>
    /// The AI proposal that produced the draft. Required unless <see cref="Source"/> is
    /// <see cref="PromptRecordSource.Manual"/>, and refused when it is.
    /// </summary>
    public Guid? AiProposalId { get; init; }

    /// <summary>The recipe this prompt was written for, or null for a prompt that is not about one.</summary>
    public Guid? RecipeId { get; init; }

    /// <summary>
    /// The exact version it was written against. Requires <see cref="RecipeId"/>, and must be a version of that
    /// same recipe.
    /// </summary>
    public Guid? RecipeVersionId { get; init; }

    /// <summary>The template that wrote the draft. Required for a generated prompt, with the two fields below.</summary>
    /// <remarks>
    /// A triple, because a version string alone is a claim: a template body can change under a version, and a
    /// record pinned by version alone then cannot say what it was written with.
    /// </remarks>
    public string? PromptTemplateId { get; init; }

    /// <inheritdoc cref="PromptTemplateId"/>
    public string? PromptTemplateVersion { get; init; }

    /// <inheritdoc cref="PromptTemplateId"/>
    public string? PromptTemplateBodyChecksum { get; init; }

    /// <summary>
    /// The generated image this prompt produced, when it produced one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Refused until 12.6, and accepted now because something can finally verify it.</strong> 12.3a
    /// deliberately left this field out: the column existed, nothing could resolve an id, and the row is
    /// immutable — so a wrong value written once could only ever be erased. The generated-image workspace now
    /// exists, the id is resolved through its facade inside the resolved workspace, and the composite foreign
    /// key makes a wrong one unrepresentable rather than merely refused.
    /// </para>
    /// <para>
    /// <strong>Set at insert or never.</strong> A prompt record cannot be updated, so a prompt saved before
    /// its image was committed has no way to gain one afterwards — which is why the save happens after the
    /// image is staged, from inside the transaction that commits both.
    /// </para>
    /// <para>
    /// There is still no <c>damAssetId</c>: nothing can verify one until 12.9, which is the same reason this
    /// field waited.
    /// </para>
    /// </remarks>
    public Guid? GeneratedImageId { get; init; }
}
