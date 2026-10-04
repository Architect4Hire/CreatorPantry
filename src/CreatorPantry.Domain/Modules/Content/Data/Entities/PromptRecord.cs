using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One prompt in the creator's library: the words that produced an image, and everything it was produced from.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Private creator content.</strong> Workspace-owned, carrying the global query filter like every other
/// creator record. <see cref="Text"/> and <see cref="GeneratedText"/> are prompt bodies and are never logged and
/// never put in an audit summary (ai.md) — a prompt is the creator's craft, and the thing a competitor would most
/// like to read.
/// </para>
/// <para>
/// <strong>Immutable</strong> by way of <see cref="IImmutableRecord"/>: every update and delete is refused at
/// <c>SaveChanges</c>, as for <see cref="ContentRevision"/>. The reason is the same and it is not tidiness — this
/// row is the evidence of what produced an image that may already be published, so editing its text afterwards
/// would make it lie about history. A corrected or reworked prompt is a new record. The consequence to know:
/// <see cref="Label"/> cannot be changed either, so a library a creator wants to reorganise later needs a separate
/// annotation row rather than an edit, which is the trade this codebase already makes for revisions.
/// </para>
/// <para>
/// <strong>No URL and no object path, anywhere.</strong> Asset lineage is an identity —
/// <see cref="GeneratedImageId"/>, <see cref="DamAssetId"/> — because a blob URL is a rendering of where bytes
/// happen to live today and is worthless as a durable reference. <c>PromptRecordModelShapeTests</c> fails on a
/// property whose name suggests one.
/// </para>
/// <para>
/// <strong>Retention: none, deliberately.</strong> This row is a few kilobytes of text and is the provenance of an
/// asset that may be live, so there is no expiry and no soft-delete column. Staged <em>images</em> get retention
/// deadlines (12.6) because bytes are expensive; that argument does not reach text. Workspace deletion cascades,
/// and the recipe pins are <c>Restrict</c>, so a recipe with prompts cannot simply be deleted.
/// </para>
/// </remarks>
public class PromptRecord : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// The channel this prompt's image is for, as a <c>ContentChannel.Key</c>. Opaque here; the write seam
    /// validates it against the catalogue.
    /// </summary>
    public string ChannelKey { get; set; } = string.Empty;

    /// <summary>What the image is for. The requirements' "content type", and editorial rather than a media type.</summary>
    public PromptImageKind ImageKind { get; set; }

    /// <summary>
    /// The authoritative prompt — what was actually sent to generate the image, after any creator edit.
    /// </summary>
    /// <remarks>
    /// Authoritative because 12.4a says the creator-edited final prompt is: a model's draft is a proposal, and this
    /// is what the creator shipped. Required; a prompt record with no prompt records nothing.
    /// </remarks>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The model's draft, when a model wrote one, kept beside the authoritative <see cref="Text"/>.
    /// </summary>
    /// <remarks>
    /// Two columns rather than a "was edited" flag, and that is the point: a flag would record that an edit
    /// happened and lose what it was, where this answers "what did the creator change" for as long as the row
    /// exists — the same reason a recipe keeps the creator's own words beside the normalised reference
    /// (recipes.md). Null for a manual prompt, and equal to <see cref="Text"/> when the creator accepted the draft
    /// unchanged.
    /// </remarks>
    public string? GeneratedText { get; set; }

    /// <summary>The creator's own short name for this prompt. Optional, and theirs to word.</summary>
    public string? Label { get; set; }

    public PromptRecordSource Source { get; set; }

    /// <summary>
    /// The AI proposal that produced the draft; set exactly when <see cref="Source"/> is not
    /// <see cref="PromptRecordSource.Manual"/>.
    /// </summary>
    public Guid? AiProposalId { get; set; }

    /// <summary>The recipe this prompt was written for, or null for a prompt that is not about one.</summary>
    public Guid? RecipeId { get; set; }

    /// <summary>
    /// The immutable recipe version it was written against. Requires <see cref="RecipeId"/>, and the composite
    /// foreign key constrains it to a version <em>of that recipe</em>.
    /// </summary>
    public Guid? RecipeVersionId { get; set; }

    /// <summary>
    /// The staged generated image this prompt produced, when one was committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No foreign key yet: <c>GeneratedImage</c> arrives in 12.6, and this gains one then — the same path
    /// <c>ContentRevision.BrandStyleGuideVersionId</c> and <c>RecipeVersion.AiProposalId</c> each took before their
    /// target existed. Until then the unique index on it is what holds the relationship honest, and it is also
    /// what makes a duplicate record on a DAM retry unrepresentable (12.3a).
    /// </para>
    /// <para>
    /// <strong>The write seam must resolve this id inside the workspace before storing it, and that requirement is
    /// not optional.</strong> Nothing here can check it — there is no table to point at — and this row is
    /// <see cref="IImmutableRecord"/>, so a value written wrongly can never be corrected afterwards. A record left
    /// holding another workspace's id, or a stale one, would then make 12.6's composite foreign key impossible to
    /// add without an erasure carve-out. Validate through the owning facade, in the resolved workspace, before
    /// insert.
    /// </para>
    /// </remarks>
    public Guid? GeneratedImageId { get; set; }

    /// <summary>The DAM asset the image became, once committed. No foreign key until 12.9.</summary>
    /// <remarks>
    /// Carries no uniqueness, unlike <see cref="GeneratedImageId"/>, because that one is the idempotency anchor: a
    /// prompt is saved when the generated image is committed, so the image is what a retry would duplicate against.
    /// Two records naming one DAM asset is therefore possible and is not treated as an error. The validate-before-
    /// insert requirement above applies here identically, and for the same reason.
    /// </remarks>
    public Guid? DamAssetId { get; set; }

    /// <summary>The prompt template that wrote the draft. Required when a model did.</summary>
    /// <remarks>
    /// A triple with the version and the body checksum, because a version string alone is a claim: a template body
    /// can change under a version, and then a record pinned by version alone cannot say what it was written with.
    /// The same pin <see cref="ContentRevision"/> carries.
    /// </remarks>
    public string? PromptTemplateId { get; set; }

    /// <inheritdoc cref="PromptTemplateId"/>
    public string? PromptTemplateVersion { get; set; }

    /// <inheritdoc cref="PromptTemplateId"/>
    public string? PromptTemplateBodyChecksum { get; set; }

    /// <summary>Not a foreign key, matching <c>Recipe</c>: authorship outlives membership.</summary>
    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
