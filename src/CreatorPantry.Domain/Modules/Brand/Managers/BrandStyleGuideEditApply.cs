namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What applying a creator's submitted edit to a working version produced, and what it did to get there.
/// </summary>
/// <param name="Changed">
/// Whether the result differs from the working version at all. <c>false</c> means the edit said what the guide
/// already said, and the caller writes no version. Decided by comparing the result with the working version
/// rather than by adding the counts up, so a bookkeeping slip cannot make a no-op write a version or a real
/// change write none.
/// </param>
public sealed record BrandStyleGuideEditResult(
    IReadOnlyList<BrandStyleGuideSectionServiceModel> Sections,
    IReadOnlyList<BrandStyleGuideRuleServiceModel> Rules,
    IReadOnlyList<BrandStyleGuideSourceServiceModel> Sources,
    int SectionsAdded,
    int SectionsReplaced,
    int SectionsCleared,
    int RulesAdded,
    int RulesRemoved,
    int SourcesCited,
    int SourcesUncited,
    bool Changed);

/// <summary>
/// Lays a creator's submitted edit over the guide's working version and reports the result. Deterministic, and
/// the one place the rule lives — the creator-typed counterpart to <see cref="BrandStyleGuideVersionMerge"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The result starts from the working version.</strong> A request that names one section changes that
/// section; everything else travels through untouched. This is the whole difference between a submitted edit and
/// a replacement guide, and it is what lets an editor save part of a long form without holding, and resending,
/// every word of the rest.
/// </para>
/// <para>
/// <strong>Sections are keyed, rules are a list, citations are a set</strong>, each following the shape of the
/// thing. A version holds at most one section per <c>(SectionKey, ChannelKey)</c>, so naming one sets or
/// replaces it and naming it with no text removes it. Rules have no identity beyond their own text and their
/// order is the creator's, so a submitted list replaces the stored one outright. Citations are pinned
/// <c>(document, version)</c> pairs, so they are added and dropped by exact pair and never silently re-pointed.
/// </para>
/// <para>
/// <strong>Where this differs from the merge</strong>: that one unions accepted guidance onto the guide, because
/// a proposal speaks only about what it proposes and cannot mean "delete this". A creator can mean exactly that,
/// so clearing a section and shortening a rule list are the two things this does that no merge may.
/// </para>
/// <para>
/// No limit is enforced here and none is checked: this computes, and Business refuses. A result past
/// <see cref="BrandPolicy.MaxStyleGuideRules"/> is still returned, because the caller needs the count to say
/// what it refused and by how much.
/// </para>
/// <para>
/// Pure: no clock, no context, no persistence, no entity. Every case is therefore assertable directly rather
/// than through whichever paths a seam happens to exercise.
/// </para>
/// </remarks>
public static class BrandStyleGuideEditApply
{
    /// <summary>
    /// The working version's content with <paramref name="edit"/> applied to it.
    /// </summary>
    /// <param name="workingSections">The working version's sections. Channel key null on everything but a variant.</param>
    /// <param name="workingRules">The working version's rules, in their stored order.</param>
    /// <param name="workingSources">The source versions the working version cites.</param>
    /// <param name="edit">The creator's submitted change, with its blanks already resolved.</param>
    public static BrandStyleGuideEditResult Apply(
        IReadOnlyList<BrandStyleGuideSectionServiceModel> workingSections,
        IReadOnlyList<BrandStyleGuideRuleServiceModel> workingRules,
        IReadOnlyList<BrandStyleGuideSourceServiceModel> workingSources,
        BrandStyleGuideEditDraft edit)
    {
        ArgumentNullException.ThrowIfNull(workingSections);
        ArgumentNullException.ThrowIfNull(workingRules);
        ArgumentNullException.ThrowIfNull(workingSources);
        ArgumentNullException.ThrowIfNull(edit);

        var sections = workingSections.ToDictionary(Key, section => section);
        var added = 0;
        var replaced = 0;
        var cleared = 0;

        foreach (var section in edit.Sections)
        {
            var key = (section.SectionKey, section.ChannelKey ?? string.Empty);
            var held = sections.TryGetValue(key, out var existing) ? existing : null;

            if (section.Body is null)
            {
                // Named with no text: the creator emptied this part of their guide. A key the version did not
                // hold is not counted and not an error — the part is absent either way.
                if (held is not null)
                {
                    sections.Remove(key);
                    cleared++;
                }

                continue;
            }

            sections[key] = new BrandStyleGuideSectionServiceModel(
                section.SectionKey, section.ChannelKey, section.Body);

            if (held is null)
            {
                added++;
            }
            else if (!string.Equals(held.Body, section.Body, StringComparison.Ordinal))
            {
                // Counted only where the text differs. Resubmitting a section unchanged is part of the no-op
                // this reports below, and calling it a replacement would make the count disagree with the
                // version that gets written. Compared verbatim, because two bodies differing only in
                // whitespace are two different pieces of prose.
                replaced++;
            }
        }

        // Null means the request said nothing about rules, which is not the same as submitting none.
        var rules = edit.Rules ?? workingRules;
        var before = Multiset(workingRules);
        var after = Multiset(rules);

        var rulesAdded = after.Sum(entry => Math.Max(0, entry.Value - Held(before, entry.Key)));
        var rulesRemoved = before.Sum(entry => Math.Max(0, entry.Value - Held(after, entry.Key)));

        var sources = workingSources.ToList();
        var uncite = edit.Uncite.ToHashSet();
        var uncited = sources.RemoveAll(uncite.Contains);
        var citedAlready = sources.ToHashSet();
        var cited = 0;

        foreach (var source in edit.Cite)
        {
            if (citedAlready.Add(source))
            {
                sources.Add(source);
                cited++;
            }
        }

        var finalSections = Order(sections.Values);
        var finalSources = Order(sources);

        return new BrandStyleGuideEditResult(
            finalSections,
            [.. rules],
            finalSources,
            added,
            replaced,
            cleared,
            rulesAdded,
            rulesRemoved,
            cited,
            uncited,

            // Sections and citations compare as sets, because neither has an order the guide depends on; rules
            // compare in order, because reordering them is a change the creator made on purpose and one they
            // are entitled to have saved.
            !Same(workingSections, finalSections)
                || !workingRules.Select(RuleKey).SequenceEqual(rules.Select(RuleKey))
                || !workingSources.ToHashSet().SetEquals(finalSources));
    }

    /// <summary>
    /// A section's identity: its key, and its channel for the one key that may repeat.
    /// </summary>
    /// <remarks>
    /// The empty string stands in for "no channel", matching the stored column, so one comparison covers both
    /// shapes and a null can never collide with a variant whose channel happens to be unset.
    /// </remarks>
    private static (BrandStyleGuideSectionKey Key, string Channel) Key(
        BrandStyleGuideSectionServiceModel section) =>
        (section.SectionKey, section.ChannelKey ?? string.Empty);

    /// <summary>
    /// A rule's identity, as <see cref="BrandStyleGuideVersionMerge"/> and the create validator define it.
    /// </summary>
    /// <remarks>
    /// Kept in step with both deliberately: "the same rule" has to mean one thing whether a creator typed it,
    /// accepted it from a proposal, or submitted it in a replacement list.
    /// </remarks>
    private static (BrandStyleGuideRuleKind Kind, string Text) RuleKey(BrandStyleGuideRuleServiceModel rule) =>
        (rule.Kind, rule.Text.Trim().ToUpperInvariant());

    private static Dictionary<(BrandStyleGuideRuleKind, string), int> Multiset(
        IReadOnlyList<BrandStyleGuideRuleServiceModel> rules) =>
        rules.GroupBy(RuleKey).ToDictionary(group => group.Key, group => group.Count());

    private static int Held(
        Dictionary<(BrandStyleGuideRuleKind, string), int> counts, (BrandStyleGuideRuleKind, string) key) =>
        counts.TryGetValue(key, out var count) ? count : 0;

    private static IReadOnlyList<BrandStyleGuideSectionServiceModel> Order(
        IEnumerable<BrandStyleGuideSectionServiceModel> sections) =>
        [
            .. sections
                .OrderBy(section => section.SectionKey)
                .ThenBy(section => section.ChannelKey, StringComparer.Ordinal),
        ];

    private static IReadOnlyList<BrandStyleGuideSourceServiceModel> Order(
        IEnumerable<BrandStyleGuideSourceServiceModel> sources) =>
        [.. sources.OrderBy(source => source.DocumentId).ThenBy(source => source.VersionNumber)];

    private static bool Same(
        IReadOnlyList<BrandStyleGuideSectionServiceModel> left,
        IReadOnlyList<BrandStyleGuideSectionServiceModel> right) =>
        left.Count == right.Count && left.ToHashSet().SetEquals(right);
}
