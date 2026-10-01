using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One source document a <see cref="BrandStyleGuideVersion"/> was written from, pinned to the exact
/// <see cref="BrandSourceDocumentVersion"/>. Interior to the guide version and write-once with it; the whole
/// row is the key.
/// </summary>
/// <remarks>
/// Pinned to a version, not a document, so replacing the upload cannot change what an existing guide version
/// cites. The foreign key also refuses removing a cited upload from under the guide.
/// </remarks>
public class BrandStyleGuideSourceLink : IWorkspaceOwned, IImmutableRecord
{
    public Guid WorkspaceId { get; set; }

    public Guid BrandStyleGuideVersionId { get; set; }

    public Guid BrandSourceDocumentVersionId { get; set; }
}
