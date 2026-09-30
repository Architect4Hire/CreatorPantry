using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>A website or reference link the creator keeps on the brand. Interior to <see cref="BrandProfile"/>.</summary>
public class BrandLink : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandProfileId { get; set; }

    public BrandLinkKind Kind { get; set; }

    /// <summary>Absolute http(s) URL, validated at the edge. Untrusted content: never fetched as a prompt.</summary>
    public string Url { get; set; } = string.Empty;

    public string? Label { get; set; }

    public int SortOrder { get; set; }
}
