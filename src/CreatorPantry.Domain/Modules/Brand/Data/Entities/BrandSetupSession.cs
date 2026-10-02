using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One creator's resumable "Create my voice" wizard session in one workspace. Workspace-owned, and private to
/// the user it belongs to: at most one per (workspace, user).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Progress, not brand.</strong> There is no tone, voice, style, example or visual field here and none
/// may be added: <see cref="DraftJson"/> is an opaque, non-canonical scratch document, and
/// <c>BrandStyleGuideVersion</c> stays the sole source of truth for style. Completing a session activates
/// nothing.
/// </para>
/// <para>
/// <see cref="UserId"/> is stamped server-side from the authenticated caller and never read from a request.
/// <see cref="DraftJson"/> is creator content: it is stored verbatim and never logged or audited.
/// </para>
/// </remarks>
public class BrandSetupSession : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The Identity account that owns this session.</summary>
    public string UserId { get; set; } = string.Empty;

    public BrandSetupSessionStatus Status { get; set; } = BrandSetupSessionStatus.InProgress;

    public string CurrentStep { get; set; } = BrandSetupSteps.Goals;

    public string FurthestStep { get; set; } = BrandSetupSteps.Goals;

    /// <summary>Step slugs in wizard order.</summary>
    public List<string> CompletedSteps { get; set; } = [];

    /// <summary>Step slugs in wizard order.</summary>
    public List<string> SkippedSteps { get; set; } = [];

    /// <summary>An opaque JSON object, at most 64 KB. Verbatim; never logged.</summary>
    public string DraftJson { get; set; } = "{}";

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
