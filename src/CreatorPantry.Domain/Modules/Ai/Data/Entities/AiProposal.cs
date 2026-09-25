using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// What one AI operation proposed, and everything needed to explain where it came from. Workspace-owned, and
/// the root of the proposal aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable.</strong> A proposal is a record of what a model said at a moment, against a pinned
/// source, under a named template. None of that can be corrected later — a different answer is a different
/// operation. The creator's decisions are recorded on the changes and on the operation's status, never by
/// editing this row.
/// </para>
/// <para>
/// At most one per operation, enforced by a unique index rather than by sharing the operation's key, so a
/// proposal keeps an identity of its own for the routes that address it.
/// </para>
/// <para>
/// There is no column here for the raw model output. It would be the structured changes over again, with
/// nothing to say which copy was authoritative, and ai.md is explicit that generated creator content is not
/// stored by default. What the creator reviews lives in <see cref="AiStructuredChange"/>, which is the
/// artifact rather than a log of one.
/// </para>
/// </remarks>
public class AiProposal : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid AiOperationId { get; set; }

    /// <summary>
    /// The exact recipe version this was computed against; null for a task that is not version-specific.
    /// </summary>
    /// <remarks>
    /// Must equal the operation's own source version. The two cannot be tied together by a constraint —
    /// that is a comparison across rows — so the write seam is what keeps them equal, and a reader that needs
    /// certainty should prefer the operation's. It is repeated here because staleness is checked against the
    /// proposal being reviewed, and a proposal that could not name its own source would have to be joined
    /// through the operation to know whether it was still applicable.
    /// </remarks>
    public Guid? SourceRecipeVersionId { get; set; }

    /// <summary>The schema version the model's output was validated against before any of it was stored.</summary>
    public string OutputSchemaVersion { get; set; } = string.Empty;

    /// <summary>The prompt template's id, as its manifest declares it.</summary>
    public string PromptTemplateId { get; set; } = string.Empty;

    /// <summary>The template's <c>major.minor.patch</c> version.</summary>
    public string PromptTemplateVersion { get; set; } = string.Empty;

    /// <summary>
    /// The verified <c>sha256:</c> checksum of the template body that produced this.
    /// </summary>
    /// <remarks>
    /// The version alone would be a claim; this is the body. A template's manifest cannot change its body
    /// without changing this value, so recording it means a stored proposal can be explained against the exact
    /// wording that produced it rather than against whatever that version says today.
    /// </remarks>
    public string PromptTemplateBodyChecksum { get; set; } = string.Empty;

    /// <summary>The provider the request went to.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>The model that answered.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>The named deployment the model was served from, when the provider has one.</summary>
    public string? ModelDeployment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<AiStructuredChange> Changes { get; set; } = [];

    public ICollection<AiWarning> Warnings { get; set; } = [];

    public ICollection<AiProposalFeedback> Feedback { get; set; } = [];
}
