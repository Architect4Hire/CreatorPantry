using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// One passage a generation's brand context was grounded on, named by id and pinned version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identifiers only, and no foreign key to the brand module's tables.</strong> The reason is the one
/// <c>AiProposalConfiguration</c> already records for its missing edge to <c>Workspaces</c>: the brand document
/// cascades from the workspace too, so a second edge would leave SQL Server with two cascade paths into this
/// table and it refuses the DDL outright. A <c>Restrict</c> edge was the alternative and is worse — it would make
/// archiving a source document fail once any generation had ever cited it.
/// </para>
/// <para>
/// The consequence is worth stating rather than discovering: these ids can outlive what they name. A reader
/// resolving one must handle absence, and a document whose extraction was later corrected keeps its passage id
/// while its words change — which is why the parent's checksum, not this row, is what proves which words were
/// used.
/// </para>
/// </remarks>
public class AiProposalBrandSource : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid AiProposalBrandContextId { get; set; }

    public Guid BrandSourceDocumentId { get; set; }

    /// <summary>The document version the assembler pinned when it read the passage.</summary>
    public int DocumentVersionNumber { get; set; }

    public Guid BrandSourcePassageId { get; set; }

    /// <summary>The passage's position within its document, as the brand module ordered it.</summary>
    public int Ordinal { get; set; }

    /// <summary>The order this passage travelled in the prompt, from zero.</summary>
    public int SortOrder { get; set; }
}
