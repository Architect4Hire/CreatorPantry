using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One creator's unsaved edit of one style guide in one workspace: where they had got to, so closing the tab
/// does not cost them their words. Workspace-owned, and private to the user it belongs to: at most one per
/// (workspace, guide, user).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scratch, not brand.</strong> There is no section, rule or citation column here and none may be
/// added: <see cref="DraftJson"/> is an opaque, non-canonical document, and
/// <see cref="BrandStyleGuideVersion"/> stays the sole source of truth for what the guide says. Nothing reads
/// this when a generation is grounded, and holding a draft activates nothing — the same division
/// <see cref="BrandSetupSession"/> draws for the wizard.
/// </para>
/// <para>
/// <strong>Why a row rather than browser storage.</strong> A guide version is immutable, so autosave cannot
/// write one every few seconds, and a draft kept only in the browser is lost by the reload that most often
/// costs a creator their work. It is also what makes
/// <see cref="BaselineVersionNumber"/> meaningful: the draft knows which version it was composed against, so a
/// version saved by somebody else in the meantime can be noticed rather than silently overwritten.
/// </para>
/// <para>
/// <see cref="UserId"/> is stamped server-side from the authenticated caller and never read from a request.
/// <see cref="DraftJson"/> is creator content: it is stored verbatim and never logged or audited.
/// </para>
/// </remarks>
public class BrandStyleGuideEditSession : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandStyleGuideId { get; set; }

    /// <summary>The Identity account whose unsaved edit this is.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// The guide's working version when this draft was composed, so a newer one written since can be reported
    /// rather than discovered by a refused save.
    /// </summary>
    public int BaselineVersionNumber { get; set; }

    /// <summary>An opaque JSON object, at most 512 KB. Verbatim; never logged.</summary>
    public string DraftJson { get; set; } = "{}";

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
