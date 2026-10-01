using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One tag applied to one source document. Interior to <see cref="BrandSourceDocument"/>, and a set: the whole
/// row is the key, so the same tag twice on one document is a key violation.
/// </summary>
public class BrandSourceDocumentTag : IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid BrandSourceDocumentId { get; set; }

    public Guid BrandSourceTagId { get; set; }
}
