using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// Everything a <see cref="BrandStyleGuide"/> said at one point. Write-once, together with its sections,
/// rules and source links: an edit adds a version, and <see cref="ImmutableRecordInterceptor"/> refuses every
/// update and delete of an existing one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>User-editable source data, not a prompt.</strong> The guide is the creator's own words, held as
/// keyed sections and ordered rules. What a generation is shown is assembled from them per task; nothing here
/// is a provider instruction.
/// </para>
/// <para>
/// <strong>No status column.</strong> A version is a draft until a <see cref="BrandStyleGuideApproval"/>
/// exists for it. Recording approval beside the version rather than on it is what lets the version be
/// immutable from the moment it is written.
/// </para>
/// </remarks>
public class BrandStyleGuideVersion : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandStyleGuideId { get; set; }

    /// <summary>1 for the guide's first version, then one more per edit. Unique per guide.</summary>
    public int VersionNumber { get; set; }

    /// <summary>The version of the same guide this one was edited from. Null on version 1.</summary>
    public Guid? ParentVersionId { get; set; }

    /// <summary>The editor's own note on why the change was made, or null.</summary>
    public string? ChangeReason { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership, as on the guide itself.</summary>
    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<BrandStyleGuideSection> Sections { get; set; } = [];

    public List<BrandStyleGuideRule> Rules { get; set; } = [];

    public List<BrandStyleGuideSourceLink> SourceLinks { get; set; } = [];
}
