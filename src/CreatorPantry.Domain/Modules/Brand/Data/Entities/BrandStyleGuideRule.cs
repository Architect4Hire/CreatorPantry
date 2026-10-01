using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One do or don't of a <see cref="BrandStyleGuideVersion"/>. Interior to the version and write-once with it;
/// ordered, because the creator's first rule is the one they reach for first.
/// </summary>
public class BrandStyleGuideRule : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandStyleGuideVersionId { get; set; }

    public BrandStyleGuideRuleKind Kind { get; set; }

    public string Text { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
