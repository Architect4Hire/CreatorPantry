using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One immutable state of a whole long-form document: its section plan, its words, and the exact sources it was
/// written against.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable</strong> by way of <see cref="IImmutableRecord"/>: every update and delete is refused at
/// <c>SaveChanges</c>. An edit to one section, a regeneration, a settled flag and a reaffirmation are each a new
/// revision of the whole document, and the creator's decision is recorded on the <see cref="LongFormDraft"/>,
/// never by editing this row.
/// </para>
/// <para>
/// <strong>The pins are the staleness contract.</strong> The brand and template pins are columns here. The
/// recipe pins are <see cref="LongFormRevisionRecipePin"/> rows, because a newsletter issue covers several
/// recipes and a piece may cover none. A revision carries its own pins, so a reference its creative context
/// later loses cannot break it.
/// </para>
/// <para>
/// The template columns name the template of the generation that produced <em>this revision</em>; each
/// section's own provenance is inside <see cref="Content"/>.
/// </para>
/// <para>
/// <see cref="Content"/> is a serialized <see cref="LongFormDocument"/> at <see cref="SchemaVersion"/>. It is
/// prose and private creator content: never logged, and never a source of recipe facts.
/// </para>
/// </remarks>
public class LongFormRevision : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid LongFormDraftId { get; set; }

    /// <summary>Sequential from 1 within one draft, unique there, and never reused.</summary>
    public int RevisionNumber { get; set; }

    /// <summary>The revision this was made from; null only for revision 1.</summary>
    public Guid? ParentRevisionId { get; set; }

    public ContentRevisionSource Source { get; set; }

    /// <summary>The AI proposal that produced it; set exactly when <see cref="Source"/> is AiGenerated.</summary>
    public Guid? AiProposalId { get; set; }

    /// <summary>The brand profile revision in force, or null when the workspace had no profile.</summary>
    public Guid? BrandProfileRevisionId { get; set; }

    /// <summary>The style guide version in force, or null when it was written without one.</summary>
    public Guid? BrandStyleGuideVersionId { get; set; }

    public string? PromptTemplateId { get; set; }

    public string? PromptTemplateVersion { get; set; }

    public string? PromptTemplateBodyChecksum { get; set; }

    /// <summary>The creative context's concurrency token when its package was assembled, or null.</summary>
    public string? CreativeContextVersion { get; set; }

    public int SchemaVersion { get; set; }

    public string Content { get; set; } = string.Empty;

    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
