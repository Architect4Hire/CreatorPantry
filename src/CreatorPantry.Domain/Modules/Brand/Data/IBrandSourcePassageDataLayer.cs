using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public interface IBrandSourcePassageDataLayer
{
    /// <inheritdoc cref="IBrandSourcePassageRepository.ListCurrentPassagesAsync"/>
    Task<IReadOnlyList<BrandSourcePassageServiceModel>> ListCurrentPassagesAsync(
        IReadOnlyCollection<BrandSourcePassageSelector> selectors,
        int perVersion,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSourcePassageRepository.ResolveOriginsAsync"/>
    Task<IReadOnlyList<BrandSourcePassageOriginServiceModel>> ResolveOriginsAsync(
        IReadOnlyCollection<Guid> passageIds, CancellationToken cancellationToken);
}

internal sealed class BrandSourcePassageDataLayer(
    IBrandSourcePassageRepository passages, IWorkspaceContext workspace) : IBrandSourcePassageDataLayer
{
    public async Task<IReadOnlyList<BrandSourcePassageServiceModel>> ListCurrentPassagesAsync(
        IReadOnlyCollection<BrandSourcePassageSelector> selectors,
        int perVersion,
        CancellationToken cancellationToken)
    {
        var rows = await passages.ListCurrentPassagesAsync(selectors, perVersion, cancellationToken);

        // Defence in depth, and the last place this can be caught. These rows become reference material in a
        // prompt and the citations a creator reads; isolation rests on the global query filter, and nothing above
        // here would notice if it stopped applying. A throw rather than a filter, because a row from another
        // workspace means a broken invariant, not a row to quietly drop.
        if (rows.FirstOrDefault(row => row.WorkspaceId != workspace.WorkspaceId) is not null)
        {
            throw new InvalidOperationException(
                "A brand source passage read returned a row outside the resolved workspace.");
        }

        return
        [
            .. rows.Select(row => new BrandSourcePassageServiceModel(
                row.PassageId, row.DocumentId, row.VersionNumber, row.Ordinal, row.Text)),
        ];
    }

    public async Task<IReadOnlyList<BrandSourcePassageOriginServiceModel>> ResolveOriginsAsync(
        IReadOnlyCollection<Guid> passageIds, CancellationToken cancellationToken)
    {
        var rows = await passages.ResolveOriginsAsync(passageIds, cancellationToken);

        // The same defence in depth the passage read carries, and it matters at least as much here: these rows
        // decide which source versions a stored guide version cites, so one from another workspace would write a
        // neighbour's document into a creator's own provenance permanently. A throw, not a filter — a row from
        // outside the resolved workspace means a broken invariant, not a row to quietly drop.
        if (rows.FirstOrDefault(row => row.WorkspaceId != workspace.WorkspaceId) is not null)
        {
            throw new InvalidOperationException(
                "A brand source passage origin read returned a row outside the resolved workspace.");
        }

        return [.. rows.Select(row => new BrandSourcePassageOriginServiceModel(
            row.PassageId, row.DocumentId, row.VersionNumber))];
    }
}
