namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>One rule as the comparer reads it: what it says and where it sat.</summary>
/// <remarks>
/// Carries <paramref name="SortOrder"/> rather than a rank, because a rank is relative to the other rules of
/// the same kind and only the comparer knows the whole side.
/// </remarks>
public sealed record BrandStyleGuideComparisonRuleInput(
    BrandStyleGuideRuleKind Kind, string Text, int SortOrder);

/// <summary>
/// One version's content as the comparer reads it: the three collections a version owns, and nothing else.
/// </summary>
/// <remarks>
/// Deliberately not the EF entities. The comparer is a pure function over values, which is what lets the
/// algebra be tested without a database and keeps a stored row's shape out of the diff's vocabulary.
/// </remarks>
public sealed record BrandStyleGuideComparisonInput(
    IReadOnlyList<BrandStyleGuideSectionServiceModel> Sections,
    IReadOnlyList<BrandStyleGuideComparisonRuleInput> Rules,
    IReadOnlyList<BrandStyleGuideSourceServiceModel> Sources);

/// <summary>
/// Compares two brand style guide versions. Deterministic, total, and the only comparison: the same two
/// versions always produce the same answer, and no model is involved in producing it.
/// </summary>
/// <remarks>
/// <para>
/// Pure: no I/O, no clock, no workspace, no randomness and no ordering that depends on a database collation.
/// Every list it returns is sorted by values it was handed.
/// </para>
/// <para>
/// It makes no authorization decision and resolves nothing. Both sides must already have been read through
/// the tenancy path; handing it two versions is an assertion that the caller was allowed to read both.
/// </para>
/// </remarks>
public static class BrandStyleGuideComparer
{
    public static BrandStyleGuideComparison Compare(
        BrandStyleGuideComparisonInput from, BrandStyleGuideComparisonInput to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return new BrandStyleGuideComparison
        {
            Sections = CompareSections(from.Sections, to.Sections),
            Rules = CompareRules(from.Rules, to.Rules),
            Sources = CompareSources(from.Sources, to.Sources),
        };
    }

    // ---- Sections ----

    private static IReadOnlyList<BrandStyleGuideSectionComparisonServiceModel> CompareSections(
        IReadOnlyList<BrandStyleGuideSectionServiceModel> from,
        IReadOnlyList<BrandStyleGuideSectionServiceModel> to)
    {
        // (key, channel) is unique within a version by index, so the first row per key is the only row.
        var fromByKey = Index(from);
        var toByKey = Index(to);

        var keys = fromByKey.Keys.Union(toByKey.Keys)
            .OrderBy(key => key.SectionKey)
            .ThenBy(key => key.ChannelKey, StringComparer.Ordinal);

        var results = new List<BrandStyleGuideSectionComparisonServiceModel>();

        foreach (var key in keys)
        {
            var before = fromByKey.GetValueOrDefault(key);
            var after = toByKey.GetValueOrDefault(key);

            var state = (before, after) switch
            {
                (null, not null) => BrandStyleGuideComparisonState.Added,
                (not null, null) => BrandStyleGuideComparisonState.Removed,

                // Ordinal: the creator's own words, compared exactly. A case or accent change is a change.
                _ when string.Equals(before!.Body, after!.Body, StringComparison.Ordinal)
                    => BrandStyleGuideComparisonState.Unchanged,
                _ => BrandStyleGuideComparisonState.Changed,
            };

            results.Add(new BrandStyleGuideSectionComparisonServiceModel(
                key.SectionKey,
                key.ChannelKey.Length == 0 ? null : key.ChannelKey,
                state,
                before?.Body,
                after?.Body));
        }

        return results;
    }

    private static Dictionary<(BrandStyleGuideSectionKey SectionKey, string ChannelKey), BrandStyleGuideSectionServiceModel> Index(
        IReadOnlyList<BrandStyleGuideSectionServiceModel> sections)
    {
        var index = new Dictionary<(BrandStyleGuideSectionKey, string), BrandStyleGuideSectionServiceModel>();

        foreach (var section in sections)
        {
            // TryAdd rather than Add: a duplicate key cannot survive the unique index, and a read path is no
            // place to throw over data the store cannot hold.
            index.TryAdd((section.SectionKey, section.ChannelKey ?? string.Empty), section);
        }

        return index;
    }

    // ---- Rules ----

    private static IReadOnlyList<BrandStyleGuideRuleComparisonServiceModel> CompareRules(
        IReadOnlyList<BrandStyleGuideComparisonRuleInput> from,
        IReadOnlyList<BrandStyleGuideComparisonRuleInput> to)
    {
        var fromRanks = Ranks(from);
        var toRanks = Ranks(to);

        var identities = fromRanks.Keys.Union(toRanks.Keys)
            .OrderBy(identity => identity.Kind)
            .ThenBy(identity => identity.Text, StringComparer.Ordinal);

        var results = new List<BrandStyleGuideRuleComparisonServiceModel>();

        foreach (var identity in identities)
        {
            var before = fromRanks.GetValueOrDefault(identity, []);
            var after = toRanks.GetValueOrDefault(identity, []);

            // One rule per (kind, text) in practice — the create path refuses a repeat — so this pairing is
            // one-to-one. It is written as a pairing anyway so a row seeded past that check still compares.
            foreach (var (fromRank, toRank) in Pair(before, after))
            {
                var state = (fromRank, toRank) switch
                {
                    (null, not null) => BrandStyleGuideComparisonState.Added,
                    (not null, null) => BrandStyleGuideComparisonState.Removed,
                    _ when fromRank == toRank => BrandStyleGuideComparisonState.Unchanged,

                    // Never Changed: the text is the identity, so a rule that reads differently is a
                    // different rule. All that is left for a surviving rule is where it sits.
                    _ => BrandStyleGuideComparisonState.Moved,
                };

                results.Add(new BrandStyleGuideRuleComparisonServiceModel(
                    identity.Kind, identity.Text, state, fromRank, toRank));
            }
        }

        return
        [
            .. results
                .OrderBy(rule => rule.Kind)
                .ThenBy(rule => rule.ToRank ?? int.MaxValue)
                .ThenBy(rule => rule.FromRank ?? int.MaxValue)
                .ThenBy(rule => rule.Text, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Each rule's 0-based position among the rules of its own kind, in stored order.
    /// </summary>
    /// <remarks>
    /// A rank rather than the stored <c>SortOrder</c>, which is unique across both kinds within a version and
    /// may be renumbered without anything having moved. Ranking within a kind is also what stops inserting a
    /// don't from reporting every do as moved.
    /// </remarks>
    private static Dictionary<(BrandStyleGuideRuleKind Kind, string Text), List<int>> Ranks(
        IReadOnlyList<BrandStyleGuideComparisonRuleInput> rules)
    {
        var ranks = new Dictionary<(BrandStyleGuideRuleKind, string), List<int>>();

        foreach (var kind in rules.Select(rule => rule.Kind).Distinct())
        {
            var ordered = rules.Where(rule => rule.Kind == kind).OrderBy(rule => rule.SortOrder).ToList();

            for (var rank = 0; rank < ordered.Count; rank++)
            {
                var identity = (kind, ordered[rank].Text);

                if (!ranks.TryGetValue(identity, out var positions))
                {
                    positions = [];
                    ranks[identity] = positions;
                }

                positions.Add(rank);
            }
        }

        return ranks;
    }

    // ---- Sources ----

    private static IReadOnlyList<BrandStyleGuideSourceComparisonServiceModel> CompareSources(
        IReadOnlyList<BrandStyleGuideSourceServiceModel> from,
        IReadOnlyList<BrandStyleGuideSourceServiceModel> to)
    {
        var fromByDocument = ByDocument(from);
        var toByDocument = ByDocument(to);

        var results = new List<BrandStyleGuideSourceComparisonServiceModel>();

        foreach (var documentId in fromByDocument.Keys.Union(toByDocument.Keys).OrderBy(id => id))
        {
            var before = fromByDocument.GetValueOrDefault(documentId, []);
            var after = toByDocument.GetValueOrDefault(documentId, []);

            // Paired within one document, so re-pinning it to another of its versions is one Changed rather
            // than a removal beside an addition.
            foreach (var (fromNumber, toNumber) in Pair(before, after))
            {
                var state = (fromNumber, toNumber) switch
                {
                    (null, not null) => BrandStyleGuideComparisonState.Added,
                    (not null, null) => BrandStyleGuideComparisonState.Removed,
                    _ when fromNumber == toNumber => BrandStyleGuideComparisonState.Unchanged,
                    _ => BrandStyleGuideComparisonState.Changed,
                };

                results.Add(new BrandStyleGuideSourceComparisonServiceModel(
                    documentId, state, fromNumber, toNumber));
            }
        }

        return
        [
            .. results
                .OrderBy(source => source.DocumentId)
                .ThenBy(source => source.ToVersionNumber ?? int.MaxValue)
                .ThenBy(source => source.FromVersionNumber ?? int.MaxValue),
        ];
    }

    private static Dictionary<Guid, List<int>> ByDocument(IReadOnlyList<BrandStyleGuideSourceServiceModel> sources)
    {
        var byDocument = new Dictionary<Guid, List<int>>();

        foreach (var source in sources)
        {
            if (!byDocument.TryGetValue(source.DocumentId, out var numbers))
            {
                numbers = [];
                byDocument[source.DocumentId] = numbers;
            }

            numbers.Add(source.VersionNumber);
        }

        return byDocument;
    }

    // ---- Shared ----

    /// <summary>
    /// Pairs two sets of positions by rank order: the lowest with the lowest, and so on, with whatever is
    /// left over on either side reported as present on that side alone.
    /// </summary>
    /// <remarks>
    /// Used where one identity legitimately occurs more than once — several citations of one document, or a
    /// rule text seeded twice. Both sequences are sorted first, so the result does not depend on the order
    /// the rows arrived in.
    /// </remarks>
    private static IEnumerable<(int? From, int? To)> Pair(List<int> from, List<int> to)
    {
        var before = from.Order().ToList();
        var after = to.Order().ToList();

        for (var index = 0; index < Math.Max(before.Count, after.Count); index++)
        {
            yield return (
                index < before.Count ? before[index] : null,
                index < after.Count ? after[index] : null);
        }
    }
}
