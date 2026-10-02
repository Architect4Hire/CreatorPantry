using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The merge rule 11A.18 writes a guide version by: what accepted guidance replaces, what it adds, what it
/// leaves alone, and when it changes nothing at all.
/// </summary>
/// <remarks>
/// Unit tests, because the rule is a pure function. Every case is assertable directly rather than through
/// whichever paths the acceptance seam happens to exercise — and the one that matters most, "nothing the
/// proposal did not speak to is lost", is the kind of property an integration test can only sample.
/// </remarks>
public sealed class BrandStyleGuideVersionMergeTests
{
    [Fact]
    public void A_section_the_working_version_does_not_hold_is_added()
    {
        var merged = Merge(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            accepted: [Section(BrandStyleGuideSectionKey.Tone, "Warm.")]);

        Assert.Equal(1, merged.SectionsAdded);
        Assert.Equal(0, merged.SectionsReplaced);
        Assert.True(merged.Changed);
        Assert.Equal(2, merged.Sections.Count);
    }

    [Fact]
    public void A_section_the_working_version_holds_is_replaced_rather_than_duplicated()
    {
        var merged = Merge(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            accepted: [Section(BrandStyleGuideSectionKey.Voice, "A friend who cooks.")]);

        Assert.Equal(0, merged.SectionsAdded);
        Assert.Equal(1, merged.SectionsReplaced);
        Assert.Equal("A friend who cooks.", Assert.Single(merged.Sections).Body);
    }

    /// <summary>
    /// The property an acceptance has to have: taking one suggestion is not starting the guide again.
    /// </summary>
    [Fact]
    public void Everything_the_accepted_guidance_does_not_speak_to_survives()
    {
        var merged = Merge(
            working:
            [
                Section(BrandStyleGuideSectionKey.Voice, "Plain."),
                Section(BrandStyleGuideSectionKey.Audience, "Home cooks."),
            ],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            workingSources: [new BrandStyleGuideSourceServiceModel(DocumentA, 1)],
            accepted: [Section(BrandStyleGuideSectionKey.Tone, "Warm.")]);

        Assert.Contains(merged.Sections, section => section.SectionKey is BrandStyleGuideSectionKey.Audience);
        Assert.Contains(merged.Sections, section => section.SectionKey is BrandStyleGuideSectionKey.Voice);
        Assert.Single(merged.Rules);
        Assert.Single(merged.Sources);
    }

    [Fact]
    public void A_channel_variant_is_held_once_per_channel_rather_than_once_overall()
    {
        var merged = Merge(
            working: [Variant("instagram", "Short.")],
            accepted: [Variant("pinterest", "Vertical.")]);

        Assert.Equal(1, merged.SectionsAdded);
        Assert.Equal(2, merged.Sections.Count);
        Assert.Equal(
            ["instagram", "pinterest"],
            merged.Sections.Select(section => section.ChannelKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_variant_for_a_channel_already_held_replaces_that_one_only()
    {
        var merged = Merge(
            working: [Variant("instagram", "Short."), Variant("pinterest", "Vertical.")],
            accepted: [Variant("instagram", "Short, and lead with the picture.")]);

        Assert.Equal(1, merged.SectionsReplaced);
        Assert.Equal(2, merged.Sections.Count);
        Assert.Equal(
            "Short, and lead with the picture.",
            merged.Sections.Single(section => section.ChannelKey == "instagram").Body);
    }

    /// <summary>
    /// A null channel key and an empty one are the same absence, which is what the stored column says too. If
    /// they were not, a section with no channel could collide with a variant whose channel was unset.
    /// </summary>
    [Fact]
    public void A_null_channel_and_an_empty_channel_name_the_same_section()
    {
        var merged = Merge(
            working: [new BrandStyleGuideSectionServiceModel(BrandStyleGuideSectionKey.Voice, string.Empty, "Plain.")],
            accepted: [new BrandStyleGuideSectionServiceModel(BrandStyleGuideSectionKey.Voice, null, "Warm.")]);

        Assert.Single(merged.Sections);
        Assert.Equal(1, merged.SectionsReplaced);
    }

    // ---- rules -----------------------------------------------------------------------------------------

    [Fact]
    public void An_accepted_rule_is_appended_after_the_working_versions_own()
    {
        var merged = Merge(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            accepted: [],
            acceptedRules: [Rule(BrandStyleGuideRuleKind.Dont, "Never pad the intro.")]);

        Assert.Equal(1, merged.RulesAdded);
        Assert.Equal(0, merged.RulesAlreadyPresent);
        Assert.Equal(
            ["Say it plainly.", "Never pad the intro."],
            merged.Rules.Select(rule => rule.Text));
    }

    /// <summary>
    /// Matched the way <c>BrandStyleGuideInput</c>'s own duplicate check matches, so accepting a rule the
    /// creator had already typed cannot produce a version the create route would have refused.
    /// </summary>
    [Theory]
    [InlineData("Say it plainly.")]
    [InlineData("  Say it plainly.  ")]
    [InlineData("SAY IT PLAINLY.")]
    public void A_rule_already_present_is_reported_rather_than_added_again(string accepted)
    {
        var merged = Merge(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            accepted: [],
            acceptedRules: [Rule(BrandStyleGuideRuleKind.Do, accepted)]);

        Assert.Equal(0, merged.RulesAdded);
        Assert.Equal(1, merged.RulesAlreadyPresent);
        Assert.Single(merged.Rules);
    }

    /// <summary>The same words pointing the other way is a different rule, and both belong in the guide.</summary>
    [Fact]
    public void The_same_text_as_a_dont_is_a_different_rule_from_a_do()
    {
        var merged = Merge(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Use contractions.")],
            accepted: [],
            acceptedRules: [Rule(BrandStyleGuideRuleKind.Dont, "Use contractions.")]);

        Assert.Equal(1, merged.RulesAdded);
        Assert.Equal(2, merged.Rules.Count);
    }

    [Fact]
    public void The_same_rule_accepted_twice_in_one_decision_lands_once()
    {
        var merged = Merge(
            working: [],
            workingRules: [],
            accepted: [],
            acceptedRules:
            [
                Rule(BrandStyleGuideRuleKind.Do, "Say it plainly."),
                Rule(BrandStyleGuideRuleKind.Do, "Say it plainly."),
            ]);

        Assert.Equal(1, merged.RulesAdded);
        Assert.Equal(1, merged.RulesAlreadyPresent);
        Assert.Single(merged.Rules);
    }

    // ---- sources ---------------------------------------------------------------------------------------

    [Fact]
    public void A_cited_source_is_added_to_what_the_working_version_already_cites()
    {
        var merged = Merge(
            working: [],
            workingRules: [],
            workingSources: [new BrandStyleGuideSourceServiceModel(DocumentA, 1)],
            accepted: [Section(BrandStyleGuideSectionKey.Tone, "Warm.")],
            acceptedSources: [new BrandStyleGuideSourceServiceModel(DocumentB, 3)]);

        Assert.Equal(1, merged.SourcesAdded);
        Assert.Equal(2, merged.Sources.Count);
    }

    [Fact]
    public void A_source_already_cited_at_the_same_version_is_not_cited_twice()
    {
        var merged = Merge(
            working: [],
            workingRules: [],
            workingSources: [new BrandStyleGuideSourceServiceModel(DocumentA, 1)],
            accepted: [Section(BrandStyleGuideSectionKey.Tone, "Warm.")],
            acceptedSources: [new BrandStyleGuideSourceServiceModel(DocumentA, 1)]);

        Assert.Equal(0, merged.SourcesAdded);
        Assert.Single(merged.Sources);
    }

    /// <summary>
    /// Two versions of one document are two citations. A guide version pins exact versions, so collapsing them
    /// would lose which text the guidance actually rests on.
    /// </summary>
    [Fact]
    public void Two_versions_of_one_document_are_two_citations()
    {
        var merged = Merge(
            working: [],
            workingRules: [],
            workingSources: [new BrandStyleGuideSourceServiceModel(DocumentA, 1)],
            accepted: [Section(BrandStyleGuideSectionKey.Tone, "Warm.")],
            acceptedSources: [new BrandStyleGuideSourceServiceModel(DocumentA, 2)]);

        Assert.Equal(1, merged.SourcesAdded);
        Assert.Equal(2, merged.Sources.Count);
    }

    // ---- the no-op -------------------------------------------------------------------------------------

    /// <summary>
    /// Accepting guidance that says what the guide already says changes nothing, and the caller writes no
    /// version — the same rule a creator's own edit follows.
    /// </summary>
    [Fact]
    public void Accepting_guidance_identical_to_the_working_version_changes_nothing()
    {
        var merged = Merge(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            accepted: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            acceptedRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")]);

        Assert.False(merged.Changed);
        Assert.Equal(0, merged.SectionsAdded);
        Assert.Equal(0, merged.SectionsReplaced);
        Assert.Equal(0, merged.RulesAdded);
        Assert.Equal(1, merged.RulesAlreadyPresent);
    }

    /// <summary>
    /// Whitespace is not normalised on a body, deliberately: two bodies differing only in spacing are two
    /// pieces of prose, and treating them as one would silently discard a creator's rewrite.
    /// </summary>
    [Fact]
    public void A_body_differing_only_in_whitespace_is_a_change()
    {
        var merged = Merge(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            accepted: [Section(BrandStyleGuideSectionKey.Voice, "Plain. ")]);

        Assert.True(merged.Changed);
        Assert.Equal(1, merged.SectionsReplaced);
    }

    [Fact]
    public void Accepting_nothing_at_all_changes_nothing()
    {
        var merged = Merge(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            accepted: []);

        Assert.False(merged.Changed);
        Assert.Single(merged.Sections);
    }

    /// <summary>
    /// A citation added with no section and no rule is still a change: the version's provenance differs.
    /// </summary>
    [Fact]
    public void A_newly_cited_source_alone_is_a_change()
    {
        var merged = Merge(
            working: [],
            workingRules: [],
            workingSources: [],
            accepted: [],
            acceptedSources: [new BrandStyleGuideSourceServiceModel(DocumentA, 1)]);

        Assert.True(merged.Changed);
        Assert.Equal(1, merged.SourcesAdded);
    }

    // ---- ordering --------------------------------------------------------------------------------------

    /// <summary>
    /// Sections come back in key order, so a version reads the same however the acceptance arrived.
    /// </summary>
    [Fact]
    public void Sections_are_ordered_by_key_and_channel()
    {
        var merged = Merge(
            working: [Variant("pinterest", "Vertical.")],
            accepted:
            [
                Section(BrandStyleGuideSectionKey.Tone, "Warm."),
                Variant("instagram", "Short."),
                Section(BrandStyleGuideSectionKey.Voice, "Plain."),
            ]);

        Assert.Equal(
            [
                BrandStyleGuideSectionKey.Voice,
                BrandStyleGuideSectionKey.Tone,
                BrandStyleGuideSectionKey.ChannelVariant,
                BrandStyleGuideSectionKey.ChannelVariant,
            ],
            merged.Sections.Select(section => section.SectionKey));

        Assert.Equal(
            ["instagram", "pinterest"],
            merged.Sections
                .Where(section => section.SectionKey is BrandStyleGuideSectionKey.ChannelVariant)
                .Select(section => section.ChannelKey));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static readonly Guid DocumentA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocumentB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static BrandStyleGuideVersionMergeResult Merge(
        IReadOnlyList<BrandStyleGuideSectionServiceModel> working,
        IReadOnlyList<BrandStyleGuideSectionServiceModel> accepted,
        IReadOnlyList<BrandStyleGuideRuleServiceModel>? workingRules = null,
        IReadOnlyList<BrandStyleGuideRuleServiceModel>? acceptedRules = null,
        IReadOnlyList<BrandStyleGuideSourceServiceModel>? workingSources = null,
        IReadOnlyList<BrandStyleGuideSourceServiceModel>? acceptedSources = null) =>
        BrandStyleGuideVersionMerge.Merge(
            working,
            workingRules ?? [],
            workingSources ?? [],
            new BrandStyleGuideProposalApplication(
                Guid.NewGuid(),
                ExpectedWorkingVersionNumber: 1,
                accepted,
                acceptedRules ?? [],
                acceptedSources ?? [],
                ChangeReason: null));

    private static BrandStyleGuideSectionServiceModel Section(BrandStyleGuideSectionKey key, string body) =>
        new(key, null, body);

    private static BrandStyleGuideSectionServiceModel Variant(string channelKey, string body) =>
        new(BrandStyleGuideSectionKey.ChannelVariant, channelKey, body);

    private static BrandStyleGuideRuleServiceModel Rule(BrandStyleGuideRuleKind kind, string text) =>
        new(kind, text);
}
