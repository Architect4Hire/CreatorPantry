using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// The brand profile exactly as it stood at one <see cref="Revision"/>. Write-once: the profile is edited in
/// place, and this is the record of every state it has been in.
/// </summary>
/// <remarks>
/// <see cref="Document"/> is versioned JSON (<see cref="SchemaVersion"/>) of the creator's facts at that
/// revision, so a generation can cite which profile revision it used. It holds nothing the profile does not:
/// no voice, tone or visual direction, and no workspace or membership identifiers beyond the owning columns.
/// <see cref="ImmutableRecordInterceptor"/> refuses every update and delete.
/// </remarks>
public class BrandProfileRevision : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandProfileId { get; set; }

    /// <summary>The profile's <c>Revision</c> this row captures. Unique per profile.</summary>
    public int Revision { get; set; }

    /// <summary>The shape of <see cref="Document"/>, so old revisions stay readable after the shape moves.</summary>
    public int SchemaVersion { get; set; }

    public string Document { get; set; } = string.Empty;

    /// <summary>The editor's own note on why the change was made, or null.</summary>
    public string? Reason { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership, as on the profile itself.</summary>
    public Guid ChangedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
