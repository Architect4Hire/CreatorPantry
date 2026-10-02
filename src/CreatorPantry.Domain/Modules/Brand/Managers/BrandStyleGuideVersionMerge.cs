namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What merging accepted guidance onto a working version produced, and what it did to get there.
/// </summary>
/// <param name="Changed">
/// Whether anything differs from the working version. <c>false</c> means the accepted guidance said what the
/// guide already said, and the caller writes no version at all.
/// </param>
public sealed record BrandStyleGuideVersionMergeResult(
    IReadOnlyList<BrandStyleGuideSectionServiceModel> Sections,
    IReadOnlyList<BrandStyleGuideRuleServiceModel> Rules,
    IReadOnlyList<BrandStyleGuideSourceServiceModel> Sources,
    int SectionsAdded,
    int SectionsReplaced,
    int RulesAdded,
    int RulesAlreadyPresent,
    int SourcesAdded,
    bool Changed);

/// <summary>
/// Lays accepted guidance over the guide's working version and reports the result. Deterministic, and the one
/// place the merge rule lives.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The new version starts from the working version, not from the accepted guidance.</strong> A creator
/// who accepts a proposed <c>Voice</c> section is replacing their <c>Voice</c> section — not discarding their
/// <c>Audience</c> section, their rules, or the sources they had already cited. Anything the proposal did not
/// speak to travels through untouched, which is the difference between accepting guidance and starting again.
/// </para>
/// <para>
/// <strong>Sections replace, rules append, sources union.</strong> Each follows the shape of the thing: a
/// version holds at most one section per <c>(SectionKey, ChannelKey)</c>, so an accepted section for a key that
/// is already there is a replacement and there is no second row for it to be. Rules are an ordered list with no
/// identity beyond their own text, so an accepted rule is appended — unless the same one is already present, in
/// which case there is nothing to append and the caller is told. Citations are a set of pinned versions, so
/// accepting guidance that rests on a document already cited adds nothing.
/// </para>
/// <para>
/// <strong>Rules match the way the create validator matches them</strong> — kind plus trimmed, upper-cased text
/// — so "the same rule twice" means the same thing when a creator types it and when they accept it. Sections do
/// not get that treatment: two bodies differing only in whitespace are two different pieces of prose, and
/// normalising one into the other would silently discard an edit.
/// </para>
/// <para>
/// <strong>No limit is enforced here and none is checked.</strong> This computes; Business refuses. A merge that
/// would exceed <see cref="BrandPolicy.MaxStyleGuideRules"/> is still computed and still reported, because the
/// caller needs the count to say what it refused and by how much — a function that threw instead could only say
/// that something was too big.
/// </para>
/// <para>
/// Pure: no clock, no context, no persistence, no entity. Every case is therefore assertable directly rather
/// than through whichever paths a seam happens to exercise.
/// </para>
/// </remarks>
public static class BrandStyleGuideVersionMerge
{
    /// <summary>
    /// The working version's content with <paramref name="accepted"/> laid over it.
    /// </summary>
    /// <param name="workingSections">The working version's sections. Channel key null on everything but a variant.</param>
    /// <param name="workingRules">The working version's rules, in their stored order.</param>
    /// <param name="workingSources">The source versions the working version already cites.</param>
    /// <param name="accepted">The accepted guidance, already translated into this module's vocabulary.</param>
    public static BrandStyleGuideVersionMergeResult Merge(
        IReadOnlyList<BrandStyleGuideSectionServiceModel> workingSections,
        IReadOnlyList<BrandStyleGuideRuleServiceModel> workingRules,
        IReadOnlyList<BrandStyleGuideSourceServiceModel> workingSources,
        BrandStyleGuideProposalApplication accepted)
    {
        ArgumentNullException.ThrowIfNull(workingSections);
        ArgumentNullException.ThrowIfNull(workingRules);
        ArgumentNullException.ThrowIfNull(workingSources);
        ArgumentNullException.ThrowIfNull(accepted);

        var sections = workingSections.ToDictionary(Key, section => section);
        var added = 0;
        var replaced = 0;

        foreach (var section in accepted.Sections)
        {
            var existing = sections.TryGetValue(Key(section), out var held) ? held : null;
            sections[Key(section)] = section;

            if (existing is null)
            {
                added++;
            }
            else if (existing.Body != section.Body)
            {
                // Counted only where the text actually differs. Accepting guidance that says what the section
                // already said is the no-op the Changed flag below is looking for, and calling it a replacement
                // would make the count disagree with the version that gets written.
                replaced++;
            }
        }

        var rules = workingRules.ToList();
        var seen = workingRules.Select(RuleKey).ToHashSet();
        var rulesAdded = 0;
        var rulesAlreadyPresent = 0;

        foreach (var rule in accepted.Rules)
        {
            if (seen.Add(RuleKey(rule)))
            {
                rules.Add(rule);
                rulesAdded++;
            }
            else
            {
                rulesAlreadyPresent++;
            }
        }

        var sources = workingSources.ToList();
        var citedAlready = workingSources.ToHashSet();
        var sourcesAdded = 0;

        foreach (var source in accepted.CitedSources)
        {
            if (citedAlready.Add(source))
            {
                sources.Add(source);
                sourcesAdded++;
            }
        }

        return new BrandStyleGuideVersionMergeResult(
            [
                .. sections.Values
                    .OrderBy(section => section.SectionKey)
                    .ThenBy(section => section.ChannelKey, StringComparer.Ordinal),
            ],
            rules,
            [.. sources.OrderBy(source => source.DocumentId).ThenBy(source => source.VersionNumber)],
            added,
            replaced,
            rulesAdded,
            rulesAlreadyPresent,
            sourcesAdded,

            // Every way the merge can have changed something, in one place. A replacement whose text matched was
            // never counted, so this is false exactly when the guide would say the same thing afterwards — which
            // is when the caller writes no version.
            added > 0 || replaced > 0 || rulesAdded > 0 || sourcesAdded > 0);
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
    /// A rule's identity, as <c>BrandStyleGuideInput</c>'s own duplicate check defines it.
    /// </summary>
    /// <remarks>
    /// Kept in step with that check deliberately: if accepting a rule the creator had already typed produced a
    /// second row, the version would hold a duplicate the create route would have refused outright.
    /// </remarks>
    private static (BrandStyleGuideRuleKind Kind, string Text) RuleKey(BrandStyleGuideRuleServiceModel rule) =>
        (rule.Kind, rule.Text.Trim().ToUpperInvariant());
}
