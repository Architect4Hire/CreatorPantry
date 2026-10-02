using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <see cref="BrandStyleGuideComparer"/>'s algebra, with no database: what each of the five states means, and
/// what the comparer refuses to claim.
/// </summary>
/// <remarks>
/// The comparer is a pure function, so these are the tests that can state the whole truth table cheaply. The
/// endpoint tests prove the same vocabulary survives the seam; they are not where an edge case belongs.
/// </remarks>
public sealed class BrandStyleGuideComparerTests
{
    private static readonly Guid DocumentOne = new("11111111-1111-1111-1111-111111111111");

    private static readonly Guid DocumentTwo = new("22222222-2222-2222-2222-222222222222");

    // ---- Sections ----

    [Fact]
    public void A_section_added_removed_changed_and_left_alone_is_each_named()
    {
        var from = Input(
            sections:
            [
                Section(BrandStyleGuideSectionKey.Voice, "Warm."),
                Section(BrandStyleGuideSectionKey.Tone, "Dry."),
                Section(BrandStyleGuideSectionKey.Audience, "Home cooks."),
            ]);
        var to = Input(
            sections:
            [
                Section(BrandStyleGuideSectionKey.Voice, "Warm."),
                Section(BrandStyleGuideSectionKey.Tone, "Warmer."),
                Section(BrandStyleGuideSectionKey.Formatting, "Short paragraphs."),
            ]);

        var sections = BrandStyleGuideComparer.Compare(from, to).Sections;

        // Listed by section key, every section either side holds, unchanged ones included.
        Assert.Equal(
            [
                BrandStyleGuideSectionKey.Voice,
                BrandStyleGuideSectionKey.Tone,
                BrandStyleGuideSectionKey.Audience,
                BrandStyleGuideSectionKey.Formatting,
            ],
            sections.Select(section => section.SectionKey));

        Assert.Equal(BrandStyleGuideComparisonState.Unchanged, State(sections, BrandStyleGuideSectionKey.Voice));
        Assert.Equal(BrandStyleGuideComparisonState.Changed, State(sections, BrandStyleGuideSectionKey.Tone));
        Assert.Equal(BrandStyleGuideComparisonState.Removed, State(sections, BrandStyleGuideSectionKey.Audience));
        Assert.Equal(BrandStyleGuideComparisonState.Added, State(sections, BrandStyleGuideSectionKey.Formatting));

        // Both bodies on a change; the absent side is null on an addition and a removal.
        var tone = sections.Single(section => section.SectionKey == BrandStyleGuideSectionKey.Tone);
        Assert.Equal("Dry.", tone.FromBody);
        Assert.Equal("Warmer.", tone.ToBody);

        var removed = sections.Single(section => section.SectionKey == BrandStyleGuideSectionKey.Audience);
        Assert.Equal("Home cooks.", removed.FromBody);
        Assert.Null(removed.ToBody);

        var added = sections.Single(section => section.SectionKey == BrandStyleGuideSectionKey.Formatting);
        Assert.Null(added.FromBody);
        Assert.Equal("Short paragraphs.", added.ToBody);
    }

    [Fact]
    public void Channel_variants_are_separate_sections_keyed_by_their_channel()
    {
        var from = Input(sections:
        [
            Section(BrandStyleGuideSectionKey.ChannelVariant, "Short.", "instagram"),
            Section(BrandStyleGuideSectionKey.ChannelVariant, "Long.", "blog"),
        ]);
        var to = Input(sections:
        [
            Section(BrandStyleGuideSectionKey.ChannelVariant, "Shorter.", "instagram"),
            Section(BrandStyleGuideSectionKey.ChannelVariant, "Long.", "blog"),
        ]);

        var sections = BrandStyleGuideComparer.Compare(from, to).Sections;

        // Two rows under one key, told apart by channel — and ordered by it.
        Assert.Equal(["blog", "instagram"], sections.Select(section => section.ChannelKey));
        Assert.Equal(BrandStyleGuideComparisonState.Unchanged, sections[0].State);
        Assert.Equal(BrandStyleGuideComparisonState.Changed, sections[1].State);
    }

    [Fact]
    public void A_body_is_compared_exactly_and_a_non_variant_section_reports_no_channel()
    {
        var comparison = BrandStyleGuideComparer.Compare(
            Input(sections: [Section(BrandStyleGuideSectionKey.Voice, "Warm.")]),
            Input(sections: [Section(BrandStyleGuideSectionKey.Voice, "warm.")]));

        var section = Assert.Single(comparison.Sections);

        // The creator's own words, so a case change is a change rather than noise.
        Assert.Equal(BrandStyleGuideComparisonState.Changed, section.State);
        Assert.Null(section.ChannelKey);
    }

    // ---- Rules ----

    [Fact]
    public void A_rule_that_only_changed_places_is_moved_and_carries_both_ranks()
    {
        var from = Input(rules:
        [
            Rule(BrandStyleGuideRuleKind.Do, "Say you", 0),
            Rule(BrandStyleGuideRuleKind.Do, "Lead with the dish", 1),
        ]);
        var to = Input(rules:
        [
            Rule(BrandStyleGuideRuleKind.Do, "Lead with the dish", 0),
            Rule(BrandStyleGuideRuleKind.Do, "Say you", 1),
        ]);

        var rules = BrandStyleGuideComparer.Compare(from, to).Rules;

        Assert.All(rules, rule => Assert.Equal(BrandStyleGuideComparisonState.Moved, rule.State));

        var lead = rules.Single(rule => rule.Text == "Lead with the dish");
        Assert.Equal(1, lead.FromRank);
        Assert.Equal(0, lead.ToRank);

        // Listed by the position they end up in, so the list reads as the "to" version does.
        Assert.Equal(["Lead with the dish", "Say you"], rules.Select(rule => rule.Text));
    }

    [Fact]
    public void A_reworded_rule_is_a_removal_and_an_addition_rather_than_a_change()
    {
        var comparison = BrandStyleGuideComparer.Compare(
            Input(rules: [Rule(BrandStyleGuideRuleKind.Do, "Say you", 0)]),
            Input(rules: [Rule(BrandStyleGuideRuleKind.Do, "Say \"you\"", 0)]));

        // The text is the identity, so a rule that reads differently is a different rule. Nothing here
        // guesses that one replaced the other.
        Assert.Equal(
            [BrandStyleGuideComparisonState.Added, BrandStyleGuideComparisonState.Removed],
            comparison.Rules.Select(rule => rule.State).Order());
        Assert.DoesNotContain(BrandStyleGuideComparisonState.Changed, comparison.Rules.Select(rule => rule.State));

        var removed = comparison.Rules.Single(rule => rule.State == BrandStyleGuideComparisonState.Removed);
        Assert.Equal(0, removed.FromRank);
        Assert.Null(removed.ToRank);
    }

    [Fact]
    public void Ranks_count_within_a_kind_so_inserting_a_dont_does_not_move_a_do()
    {
        // SortOrder is unique across both kinds, so adding a don't at the top renumbers every do.
        var from = Input(rules:
        [
            Rule(BrandStyleGuideRuleKind.Do, "Say you", 0),
            Rule(BrandStyleGuideRuleKind.Dont, "Shout", 1),
        ]);
        var to = Input(rules:
        [
            Rule(BrandStyleGuideRuleKind.Dont, "Pad it out", 0),
            Rule(BrandStyleGuideRuleKind.Do, "Say you", 1),
            Rule(BrandStyleGuideRuleKind.Dont, "Shout", 2),
        ]);

        var rules = BrandStyleGuideComparer.Compare(from, to).Rules;

        // The do never moved relative to the other dos, even though its sort order went from 0 to 1. Ranking
        // across both kinds would have reported it as moved here, which is the defect this guards.
        var say = rules.Single(rule => rule.Text == "Say you");
        Assert.Equal(BrandStyleGuideComparisonState.Unchanged, say.State);
        Assert.Equal(0, say.FromRank);
        Assert.Equal(0, say.ToRank);

        Assert.Equal(
            BrandStyleGuideComparisonState.Added,
            rules.Single(rule => rule.Text == "Pad it out").State);

        // The surviving don't did move, and for a real reason: another don't was inserted ahead of it.
        var shout = rules.Single(rule => rule.Text == "Shout");
        Assert.Equal(BrandStyleGuideComparisonState.Moved, shout.State);
        Assert.Equal(0, shout.FromRank);
        Assert.Equal(1, shout.ToRank);
    }

    [Fact]
    public void Renumbering_sort_orders_without_moving_anything_reports_no_changes()
    {
        var from = Input(rules:
        [
            Rule(BrandStyleGuideRuleKind.Do, "First", 0),
            Rule(BrandStyleGuideRuleKind.Do, "Second", 1),
        ]);
        var to = Input(rules:
        [
            Rule(BrandStyleGuideRuleKind.Do, "First", 40),
            Rule(BrandStyleGuideRuleKind.Do, "Second", 90),
        ]);

        var comparison = BrandStyleGuideComparer.Compare(from, to);

        Assert.False(comparison.HasChanges);
        Assert.All(comparison.Rules, rule => Assert.Equal(BrandStyleGuideComparisonState.Unchanged, rule.State));
    }

    [Fact]
    public void A_rule_text_that_occurs_twice_is_paired_by_position_rather_than_doubled()
    {
        // The create path refuses a repeated rule, so this can only arrive from a direct write. It still has
        // to compare rather than throw or report one row twice.
        var from = Input(rules:
        [
            Rule(BrandStyleGuideRuleKind.Do, "Say you", 0),
            Rule(BrandStyleGuideRuleKind.Do, "Say you", 1),
        ]);
        var to = Input(rules: [Rule(BrandStyleGuideRuleKind.Do, "Say you", 0)]);

        var rules = BrandStyleGuideComparer.Compare(from, to).Rules;

        Assert.Equal(2, rules.Count);
        Assert.Equal(
            [BrandStyleGuideComparisonState.Unchanged, BrandStyleGuideComparisonState.Removed],
            rules.Select(rule => rule.State));
    }

    // ---- Sources ----

    [Fact]
    public void Re_pinning_a_citation_to_another_version_of_the_same_document_is_one_change()
    {
        var from = Input(sources: [Source(DocumentOne, 1), Source(DocumentTwo, 3)]);
        var to = Input(sources: [Source(DocumentOne, 2), Source(DocumentTwo, 3)]);

        var sources = BrandStyleGuideComparer.Compare(from, to).Sources;

        var repinned = sources.Single(source => source.DocumentId == DocumentOne);
        Assert.Equal(BrandStyleGuideComparisonState.Changed, repinned.State);
        Assert.Equal(1, repinned.FromVersionNumber);
        Assert.Equal(2, repinned.ToVersionNumber);

        Assert.Equal(
            BrandStyleGuideComparisonState.Unchanged,
            sources.Single(source => source.DocumentId == DocumentTwo).State);
    }

    [Fact]
    public void A_citation_added_and_one_dropped_are_each_named()
    {
        var sources = BrandStyleGuideComparer.Compare(
            Input(sources: [Source(DocumentOne, 1)]),
            Input(sources: [Source(DocumentTwo, 1)])).Sources;

        Assert.Equal(BrandStyleGuideComparisonState.Removed, sources.Single(source => source.DocumentId == DocumentOne).State);
        Assert.Equal(BrandStyleGuideComparisonState.Added, sources.Single(source => source.DocumentId == DocumentTwo).State);
        Assert.Null(sources.Single(source => source.DocumentId == DocumentTwo).FromVersionNumber);
    }

    [Fact]
    public void Several_citations_of_one_document_are_paired_within_that_document()
    {
        var from = Input(sources: [Source(DocumentOne, 1), Source(DocumentOne, 2)]);
        var to = Input(sources: [Source(DocumentOne, 2), Source(DocumentOne, 5)]);

        var sources = BrandStyleGuideComparer.Compare(from, to).Sources;

        // Sorted ascending and paired by position: 1→2 and 2→5. Both changed; nothing crosses documents.
        Assert.Equal(2, sources.Count);
        Assert.All(sources, source => Assert.Equal(DocumentOne, source.DocumentId));
        Assert.All(sources, source => Assert.Equal(BrandStyleGuideComparisonState.Changed, source.State));
        Assert.Equal([(1, 2), (2, 5)], sources.Select(source => (source.FromVersionNumber, source.ToVersionNumber)));
    }

    // ---- The whole comparison ----

    [Fact]
    public void A_version_compared_with_itself_has_no_changes_in_any_part()
    {
        var side = Input(
            sections: [Section(BrandStyleGuideSectionKey.Voice, "Warm.")],
            rules: [Rule(BrandStyleGuideRuleKind.Do, "Say you", 0)],
            sources: [Source(DocumentOne, 1)]);

        var comparison = BrandStyleGuideComparer.Compare(side, side);

        Assert.False(comparison.HasChanges);
        Assert.Equal(BrandStyleGuideComparisonState.Unchanged, Assert.Single(comparison.Sections).State);
        Assert.Equal(BrandStyleGuideComparisonState.Unchanged, Assert.Single(comparison.Rules).State);
        Assert.Equal(BrandStyleGuideComparisonState.Unchanged, Assert.Single(comparison.Sources).State);
    }

    [Fact]
    public void Two_empty_versions_compare_to_an_empty_comparison()
    {
        var comparison = BrandStyleGuideComparer.Compare(Input(), Input());

        Assert.False(comparison.HasChanges);
        Assert.Empty(comparison.Sections);
        Assert.Empty(comparison.Rules);
        Assert.Empty(comparison.Sources);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void HasChanges_is_true_when_any_one_part_differs(bool section, bool rule, bool source)
    {
        var from = Input(
            sections: [Section(BrandStyleGuideSectionKey.Voice, "Warm.")],
            rules: [Rule(BrandStyleGuideRuleKind.Do, "Say you", 0)],
            sources: [Source(DocumentOne, 1)]);
        var to = Input(
            sections: [Section(BrandStyleGuideSectionKey.Voice, section ? "Cool." : "Warm.")],
            rules: [Rule(BrandStyleGuideRuleKind.Do, rule ? "Say thou" : "Say you", 0)],
            sources: [Source(DocumentOne, source ? 2 : 1)]);

        Assert.True(BrandStyleGuideComparer.Compare(from, to).HasChanges);
    }

    [Fact]
    public void The_comparison_is_the_reverse_of_itself_when_the_sides_are_swapped()
    {
        var earlier = Input(
            sections: [Section(BrandStyleGuideSectionKey.Voice, "Warm.")],
            sources: [Source(DocumentOne, 1)]);
        var later = Input(
            sections: [Section(BrandStyleGuideSectionKey.Tone, "Dry.")],
            sources: [Source(DocumentOne, 2)]);

        var forward = BrandStyleGuideComparer.Compare(earlier, later);
        var backward = BrandStyleGuideComparer.Compare(later, earlier);

        Assert.Equal(
            BrandStyleGuideComparisonState.Removed,
            forward.Sections.Single(section => section.SectionKey == BrandStyleGuideSectionKey.Voice).State);
        Assert.Equal(
            BrandStyleGuideComparisonState.Added,
            backward.Sections.Single(section => section.SectionKey == BrandStyleGuideSectionKey.Voice).State);

        // A re-pin reads as a change either way round, with the numbers the other way about.
        Assert.Equal((1, 2), Numbers(forward));
        Assert.Equal((2, 1), Numbers(backward));

        static (int?, int?) Numbers(BrandStyleGuideComparison comparison)
        {
            var source = Assert.Single(comparison.Sources);

            return (source.FromVersionNumber, source.ToVersionNumber);
        }
    }

    // ---- Harness ----

    private static BrandStyleGuideComparisonInput Input(
        IReadOnlyList<BrandStyleGuideSectionServiceModel>? sections = null,
        IReadOnlyList<BrandStyleGuideComparisonRuleInput>? rules = null,
        IReadOnlyList<BrandStyleGuideSourceServiceModel>? sources = null) =>
        new(sections ?? [], rules ?? [], sources ?? []);

    private static BrandStyleGuideSectionServiceModel Section(
        BrandStyleGuideSectionKey key, string body, string? channelKey = null) => new(key, channelKey, body);

    private static BrandStyleGuideComparisonRuleInput Rule(
        BrandStyleGuideRuleKind kind, string text, int sortOrder) => new(kind, text, sortOrder);

    private static BrandStyleGuideSourceServiceModel Source(Guid documentId, int versionNumber) =>
        new(documentId, versionNumber);

    private static BrandStyleGuideComparisonState State(
        IReadOnlyList<BrandStyleGuideSectionComparisonServiceModel> sections, BrandStyleGuideSectionKey key) =>
        sections.Single(section => section.SectionKey == key).State;
}
