using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Business;

public interface IBrandSourcePassageBusiness
{
    /// <inheritdoc cref="Facade.IBrandSourcePassageFacade.ListPassagesAsync"/>
    Task<BrandSourcePassageSetServiceModel> ListPassagesAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListDocumentsWithTextAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IBrandSourcePassageFacade.ResolveOriginsAsync"/>
    Task<IReadOnlyList<BrandSourcePassageOriginServiceModel>> ResolveOriginsAsync(
        IReadOnlyList<Guid> passageIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBrandSourcePassageBusiness"/>
internal sealed class BrandSourcePassageBusiness(IBrandSourcePassageDataLayer dataLayer) : IBrandSourcePassageBusiness
{
    public async Task<BrandSourcePassageSetServiceModel> ListPassagesAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        // Deduplicated and capped here rather than trusted from the caller: this is a bound on how much
        // creator text one request can assemble, and a caller that named the same document twice must not get
        // twice the budget for it.
        var asked = selectors.Distinct().ToList();
        var wanted = asked.Take(BrandPolicy.MaxGroundingSourceVersions).ToList();

        if (wanted.Count == 0)
        {
            return new BrandSourcePassageSetServiceModel([], []);
        }

        var found = await dataLayer.ListCurrentPassagesAsync(
            wanted, BrandPolicy.MaxGroundingPassagesPerVersion, cancellationToken);

        var passages = WithinBudget(found);

        var supplied = passages
            .Select(passage => new BrandSourcePassageSelector(passage.DocumentId, passage.VersionNumber))
            .ToHashSet();

        // Measured against everything the caller asked for, not against what survived this method's own caps.
        // A selector trimmed by the version cap is as absent from the answer as one whose document has no
        // indexed text, and a caller grounding a proposal on this has to be able to say it read two of the four
        // documents it was given rather than presenting an answer that looks as well-sourced as a complete one.
        var unavailable = asked.Where(selector => !supplied.Contains(selector)).ToList();

        return new BrandSourcePassageSetServiceModel(passages, unavailable);
    }

    public async Task<IReadOnlyList<Guid>> ListDocumentsWithTextAsync(
        IReadOnlyList<BrandSourcePassageSelector> selectors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        var withText = new HashSet<Guid>();

        // In batches of the number of versions one read may take, so the read's round-robin budget gives every
        // version with any indexed text at least one passage: "appears in the answer" is then exact, and a long
        // list is not silently cut at the first batch.
        foreach (var batch in selectors.Distinct().Chunk(BrandPolicy.MaxGroundingSourceVersions))
        {
            var supplied = await ListPassagesAsync(batch, cancellationToken);

            withText.UnionWith(supplied.Passages.Select(passage => passage.DocumentId));
        }

        return [.. withText];
    }

    public async Task<IReadOnlyList<BrandSourcePassageOriginServiceModel>> ResolveOriginsAsync(
        IReadOnlyList<Guid> passageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passageIds);

        // Deduplicated and capped the way the passage read is, and against the same ceiling: a citation list can
        // be no longer than the passages one proposal was allowed to be grounded in, so a caller asking about
        // more than that is asking about passages no request of ours could have offered.
        var wanted = passageIds
            .Where(passageId => passageId != Guid.Empty)
            .Distinct()
            .Take(BrandPolicy.MaxGroundingPassages)
            .ToList();

        return wanted.Count == 0
            ? []
            : await dataLayer.ResolveOriginsAsync(wanted, cancellationToken);
    }

    /// <summary>
    /// Trims the passages to the overall budget by taking one from each version in turn.
    /// </summary>
    /// <remarks>
    /// <strong>Round-robin rather than the first N.</strong> The per-version cap times the version cap exceeds
    /// the overall budget, so something has to go, and taking the first N would spend the whole budget on the
    /// earliest documents and drop the last ones entirely. That would be the one outcome this seam must not
    /// produce: a document that supplied passages is absent from
    /// <see cref="BrandSourcePassageSetServiceModel.Unavailable"/>, so a caller would ground on part of the
    /// selection while believing it had read all of it. Taking in turn means every version that has a passage
    /// contributes one before any contributes a second.
    /// </remarks>
    private static IReadOnlyList<BrandSourcePassageServiceModel> WithinBudget(
        IReadOnlyList<BrandSourcePassageServiceModel> found)
    {
        if (found.Count <= BrandPolicy.MaxGroundingPassages)
        {
            return found;
        }

        var byVersion = found
            .GroupBy(passage => (passage.DocumentId, passage.VersionNumber))
            .Select(group => group.ToList())
            .ToList();

        var taken = new List<BrandSourcePassageServiceModel>(BrandPolicy.MaxGroundingPassages);

        for (var round = 0; taken.Count < BrandPolicy.MaxGroundingPassages; round++)
        {
            var any = false;

            foreach (var version in byVersion)
            {
                if (round >= version.Count)
                {
                    continue;
                }

                if (taken.Count == BrandPolicy.MaxGroundingPassages)
                {
                    break;
                }

                taken.Add(version[round]);
                any = true;
            }

            if (!any)
            {
                break;
            }
        }

        // Back into document and reading order: the round-robin decided what survives, not what order a caller
        // reads it in, and a passage list that jumped between documents would be harder to cite against.
        return
        [
            .. taken
                .OrderBy(passage => passage.DocumentId)
                .ThenBy(passage => passage.VersionNumber)
                .ThenBy(passage => passage.Ordinal),
        ];
    }
}
