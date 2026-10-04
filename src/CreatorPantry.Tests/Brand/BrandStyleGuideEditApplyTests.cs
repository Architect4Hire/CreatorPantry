using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The rule 11A.15 writes a guide version by: what a submitted edit sets, replaces, clears and leaves alone,
/// and when it changes nothing at all.
/// </summary>
/// <remarks>
/// Unit tests, because the rule is a pure function — the same reason <see cref="BrandStyleGuideVersionMergeTests"/>
/// gives for the merge beside it. The property that matters most, "nothing the request did not name is lost", is
/// the kind an integration test can only sample.
/// </remarks>
public sealed class BrandStyleGuideEditApplyTests
{
    // ---- sections --------------------------------------------------------------------------------------

    [Fact]
    public void A_section_the_working_version_does_not_hold_is_added()
    {
        var applied = Apply(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            sections: [Set(BrandStyleGuideSectionKey.Tone, "Warm.")]);

        Assert.Equal(1, applied.SectionsAdded);
        Assert.Equal(0, applied.SectionsReplaced);
        Assert.Equal(0, applied.SectionsCleared);
        Assert.True(applied.Changed);
        Assert.Equal(2, applied.Sections.Count);
    }

    [Fact]
    public void A_section_the_working_version_holds_is_replaced_rather_than_duplicated()
    {
        var applied = Apply(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            sections: [Set(BrandStyleGuideSectionKey.Voice, "A friend who cooks.")]);

        Assert.Equal(1, applied.SectionsReplaced);
        Assert.Equal("A friend who cooks.", Assert.Single(applied.Sections).Body);
    }

    /// <summary>
    /// The thing a creator can mean and a proposal cannot: this part of my guide should not be there.
    /// </summary>
    [Fact]
    public void A_section_named_with_no_text_is_cleared()
    {
        var applied = Apply(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain."), Section(BrandStyleGuideSectionKey.Tone, "Warm.")],
            sections: [Clear(BrandStyleGuideSectionKey.Tone)]);

        Assert.Equal(1, applied.SectionsCleared);
        Assert.True(applied.Changed);
        Assert.Equal(BrandStyleGuideSectionKey.Voice, Assert.Single(applied.Sections).SectionKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Clearing_a_section_the_version_does_not_hold_changes_nothing(string? body)
    {
        var applied = Apply(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            sections: [new BrandStyleGuideSectionEdit(BrandStyleGuideSectionKey.Tone, null, Blank(body))]);

        Assert.Equal(0, applied.SectionsCleared);
        Assert.False(applied.Changed);
        Assert.Single(applied.Sections);
    }

    /// <summary>The property a submitted edit has to have: saving one part is not rewriting the guide.</summary>
    [Fact]
    public void Everything_the_request_does_not_name_survives()
    {
        var applied = Apply(
            working:
            [
                Section(BrandStyleGuideSectionKey.Voice, "Plain."),
                Section(BrandStyleGuideSectionKey.Audience, "Home cooks."),
            ],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            workingSources: [Source(DocumentA, 1)],
            sections: [Set(BrandStyleGuideSectionKey.Tone, "Warm.")]);

        Assert.Contains(applied.Sections, section => section.SectionKey is BrandStyleGuideSectionKey.Voice);
        Assert.Contains(applied.Sections, section => section.SectionKey is BrandStyleGuideSectionKey.Audience);
        Assert.Single(applied.Rules);
        Assert.Single(applied.Sources);
    }

    [Fact]
    public void A_variant_for_a_channel_already_held_replaces_that_one_only()
    {
        var applied = Apply(
            working: [Variant("instagram", "Short."), Variant("pinterest", "Vertical.")],
            sections: [SetVariant("instagram", "Short, and lead with the picture.")]);

        Assert.Equal(1, applied.SectionsReplaced);
        Assert.Equal(2, applied.Sections.Count);
        Assert.Equal(
            "Short, and lead with the picture.",
            applied.Sections.Single(section => section.ChannelKey == "instagram").Body);
    }

    [Fact]
    public void Clearing_one_variant_leaves_the_other_channels_alone()
    {
        var applied = Apply(
            working: [Variant("instagram", "Short."), Variant("pinterest", "Vertical.")],
            sections: [new BrandStyleGuideSectionEdit(BrandStyleGuideSectionKey.ChannelVariant, "instagram", null)]);

        Assert.Equal(1, applied.SectionsCleared);
        Assert.Equal("pinterest", Assert.Single(applied.Sections).ChannelKey);
    }

    /// <summary>
    /// A null channel key and an empty one are the same absence, which is what the stored column says too — so a
    /// section with no channel can never collide with a variant whose channel was unset.
    /// </summary>
    [Fact]
    public void A_null_channel_and_an_empty_channel_name_the_same_section()
    {
        var applied = Apply(
            working: [new BrandStyleGuideSectionServiceModel(BrandStyleGuideSectionKey.Voice, string.Empty, "Plain.")],
            sections: [Set(BrandStyleGuideSectionKey.Voice, "Warm.")]);

        Assert.Single(applied.Sections);
        Assert.Equal(1, applied.SectionsReplaced);
    }

    [Fact]
    public void A_section_resubmitted_word_for_word_changes_nothing()
    {
        var applied = Apply(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            sections: [Set(BrandStyleGuideSectionKey.Voice, "Plain.")]);

        Assert.Equal(0, applied.SectionsReplaced);
        Assert.False(applied.Changed);
    }

    /// <summary>
    /// Two bodies differing only in whitespace are two different pieces of prose, so this is a change and the
    /// creator's spacing is what gets stored. Normalising one into the other would discard an edit.
    /// </summary>
    [Fact]
    public void A_section_differing_only_in_whitespace_is_a_change()
    {
        var applied = Apply(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            sections: [Set(BrandStyleGuideSectionKey.Voice, "Plain. ")]);

        Assert.Equal(1, applied.SectionsReplaced);
        Assert.True(applied.Changed);
        Assert.Equal("Plain. ", Assert.Single(applied.Sections).Body);
    }

    // ---- rules -----------------------------------------------------------------------------------------

    [Fact]
    public void A_submitted_rule_list_replaces_the_stored_one_outright()
    {
        var applied = Apply(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly."), Rule(BrandStyleGuideRuleKind.Dont, "Never pad the intro.")],
            rules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")]);

        Assert.Equal(0, applied.RulesAdded);
        Assert.Equal(1, applied.RulesRemoved);
        Assert.Equal(["Say it plainly."], applied.Rules.Select(rule => rule.Text));
        Assert.True(applied.Changed);
    }

    [Fact]
    public void An_empty_rule_list_clears_every_rule()
    {
        var applied = Apply(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            rules: []);

        Assert.Equal(1, applied.RulesRemoved);
        Assert.Empty(applied.Rules);
        Assert.True(applied.Changed);
    }

    /// <summary>
    /// The distinction the whole null-versus-empty rule exists for: a request that says nothing about rules is
    /// not a request to delete them.
    /// </summary>
    [Fact]
    public void A_request_that_does_not_mention_rules_keeps_them()
    {
        var applied = Apply(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            sections: [Set(BrandStyleGuideSectionKey.Voice, "Plain.")]);

        Assert.Single(applied.Rules);
        Assert.Equal(0, applied.RulesRemoved);
    }

    [Theory]
    [InlineData("Say it plainly.")]
    [InlineData("  Say it plainly.  ")]
    [InlineData("SAY IT PLAINLY.")]
    public void A_rule_already_present_is_not_counted_as_added(string submitted)
    {
        var applied = Apply(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            rules: [Rule(BrandStyleGuideRuleKind.Do, submitted)]);

        Assert.Equal(0, applied.RulesAdded);
        Assert.Equal(0, applied.RulesRemoved);
    }

    /// <summary>
    /// Reordering is a change the creator made on purpose, so it writes a version — and it neither adds nor
    /// removes a rule, which is what the counts say.
    /// </summary>
    [Fact]
    public void Reordering_the_rules_is_a_change_that_adds_and_removes_nothing()
    {
        var applied = Apply(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "First."), Rule(BrandStyleGuideRuleKind.Do, "Second.")],
            rules: [Rule(BrandStyleGuideRuleKind.Do, "Second."), Rule(BrandStyleGuideRuleKind.Do, "First.")]);

        Assert.Equal(0, applied.RulesAdded);
        Assert.Equal(0, applied.RulesRemoved);
        Assert.True(applied.Changed);
        Assert.Equal(["Second.", "First."], applied.Rules.Select(rule => rule.Text));
    }

    [Fact]
    public void A_rule_list_resubmitted_unchanged_changes_nothing()
    {
        var applied = Apply(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            rules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")]);

        Assert.False(applied.Changed);
    }

    /// <summary>A rule of each kind with the same words is two rules, as the create validator also has it.</summary>
    [Fact]
    public void The_same_words_as_a_do_and_as_a_dont_are_two_rules()
    {
        var applied = Apply(
            working: [],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Mention the pan size.")],
            rules:
            [
                Rule(BrandStyleGuideRuleKind.Do, "Mention the pan size."),
                Rule(BrandStyleGuideRuleKind.Dont, "Mention the pan size."),
            ]);

        Assert.Equal(1, applied.RulesAdded);
        Assert.Equal(2, applied.Rules.Count);
    }

    // ---- citations -------------------------------------------------------------------------------------

    [Fact]
    public void A_cited_version_is_added_once()
    {
        var applied = Apply(
            working: [],
            workingSources: [Source(DocumentA, 1)],
            cite: [Source(DocumentB, 2)]);

        Assert.Equal(1, applied.SourcesCited);
        Assert.Equal(2, applied.Sources.Count);
        Assert.True(applied.Changed);
    }

    [Fact]
    public void Citing_a_version_already_cited_changes_nothing()
    {
        var applied = Apply(
            working: [],
            workingSources: [Source(DocumentA, 1)],
            cite: [Source(DocumentA, 1)]);

        Assert.Equal(0, applied.SourcesCited);
        Assert.False(applied.Changed);
        Assert.Single(applied.Sources);
    }

    [Fact]
    public void An_uncited_version_is_dropped()
    {
        var applied = Apply(
            working: [],
            workingSources: [Source(DocumentA, 1), Source(DocumentB, 1)],
            uncite: [Source(DocumentA, 1)]);

        Assert.Equal(1, applied.SourcesUncited);
        Assert.Equal(DocumentB, Assert.Single(applied.Sources).DocumentId);
    }

    /// <summary>Not an error: the citation is already gone, which is the state the request asked for.</summary>
    [Fact]
    public void Unciting_a_version_that_is_not_cited_changes_nothing()
    {
        var applied = Apply(
            working: [],
            workingSources: [Source(DocumentA, 1)],
            uncite: [Source(DocumentB, 9)]);

        Assert.Equal(0, applied.SourcesUncited);
        Assert.False(applied.Changed);
    }

    /// <summary>
    /// Re-pinning a citation to a document's newer version: by exact pair on both sides, because a version may
    /// legitimately cite two versions of one document and "stop citing D" would be ambiguous.
    /// </summary>
    [Fact]
    public void Re_pinning_a_citation_is_an_uncite_and_a_cite_of_the_same_document()
    {
        var applied = Apply(
            working: [],
            workingSources: [Source(DocumentA, 1)],
            cite: [Source(DocumentA, 2)],
            uncite: [Source(DocumentA, 1)]);

        Assert.Equal(1, applied.SourcesCited);
        Assert.Equal(1, applied.SourcesUncited);
        Assert.Equal(2, Assert.Single(applied.Sources).VersionNumber);
    }

    [Fact]
    public void Two_versions_of_one_document_may_both_be_cited()
    {
        var applied = Apply(
            working: [],
            workingSources: [Source(DocumentA, 1)],
            cite: [Source(DocumentA, 2)]);

        Assert.Equal(2, applied.Sources.Count);
        Assert.Equal([1, 2], applied.Sources.Select(source => source.VersionNumber));
    }

    // ---- ordering and no-ops ---------------------------------------------------------------------------

    [Fact]
    public void Sections_are_ordered_by_key_and_channel()
    {
        var applied = Apply(
            working: [Variant("pinterest", "Vertical.")],
            sections:
            [
                Set(BrandStyleGuideSectionKey.Tone, "Warm."),
                SetVariant("instagram", "Short."),
                Set(BrandStyleGuideSectionKey.Voice, "Plain."),
            ]);

        Assert.Equal(
            [
                BrandStyleGuideSectionKey.Voice,
                BrandStyleGuideSectionKey.Tone,
                BrandStyleGuideSectionKey.ChannelVariant,
                BrandStyleGuideSectionKey.ChannelVariant,
            ],
            applied.Sections.Select(section => section.SectionKey));

        Assert.Equal(
            ["instagram", "pinterest"],
            applied.Sections
                .Where(section => section.SectionKey is BrandStyleGuideSectionKey.ChannelVariant)
                .Select(section => section.ChannelKey));
    }

    /// <summary>
    /// Everything resubmitted as it stands: the case a creator reaches by pressing Save twice, and the one that
    /// must not fill a history with versions saying nothing new.
    /// </summary>
    [Fact]
    public void Resubmitting_the_whole_working_version_changes_nothing()
    {
        var applied = Apply(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain."), Variant("instagram", "Short.")],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            workingSources: [Source(DocumentA, 1)],
            sections: [Set(BrandStyleGuideSectionKey.Voice, "Plain."), SetVariant("instagram", "Short.")],
            rules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            cite: [Source(DocumentA, 1)]);

        Assert.False(applied.Changed);
    }

    /// <summary>
    /// Clearing everything is allowed and is a change. The result is an empty version, which approval and
    /// activation are where the product refuses — not the save that recorded what the creator did.
    /// </summary>
    [Fact]
    public void Clearing_every_part_produces_an_empty_result_rather_than_a_refusal()
    {
        var applied = Apply(
            working: [Section(BrandStyleGuideSectionKey.Voice, "Plain.")],
            workingRules: [Rule(BrandStyleGuideRuleKind.Do, "Say it plainly.")],
            sections: [Clear(BrandStyleGuideSectionKey.Voice)],
            rules: []);

        Assert.True(applied.Changed);
        Assert.Empty(applied.Sections);
        Assert.Empty(applied.Rules);
    }

    /// <summary>
    /// No limit is enforced here: this computes, and Business refuses — which is what lets the refusal say by
    /// how much.
    /// </summary>
    [Fact]
    public void A_result_past_a_limit_is_still_computed_and_returned()
    {
        var applied = Apply(
            working: [],
            rules:
            [
                .. Enumerable.Range(0, BrandPolicy.MaxStyleGuideRules + 5)
                    .Select(index => Rule(BrandStyleGuideRuleKind.Do, $"Rule {index}.")),
            ]);

        Assert.Equal(BrandPolicy.MaxStyleGuideRules + 5, applied.Rules.Count);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static readonly Guid DocumentA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid DocumentB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static BrandStyleGuideEditResult Apply(
        IReadOnlyList<BrandStyleGuideSectionServiceModel> working,
        IReadOnlyList<BrandStyleGuideRuleServiceModel>? workingRules = null,
        IReadOnlyList<BrandStyleGuideSourceServiceModel>? workingSources = null,
        IReadOnlyList<BrandStyleGuideSectionEdit>? sections = null,
        IReadOnlyList<BrandStyleGuideRuleServiceModel>? rules = null,
        IReadOnlyList<BrandStyleGuideSourceServiceModel>? cite = null,
        IReadOnlyList<BrandStyleGuideSourceServiceModel>? uncite = null) =>
        BrandStyleGuideEditApply.Apply(
            working,
            workingRules ?? [],
            workingSources ?? [],
            new BrandStyleGuideEditDraft(
                ExpectedWorkingVersionNumber: 1,
                ChangeReason: null,
                sections ?? [],
                rules,
                cite ?? [],
                uncite ?? []));

    private static BrandStyleGuideSectionServiceModel Section(BrandStyleGuideSectionKey key, string body) =>
        new(key, null, body);

    private static BrandStyleGuideSectionServiceModel Variant(string channelKey, string body) =>
        new(BrandStyleGuideSectionKey.ChannelVariant, channelKey, body);

    private static BrandStyleGuideSectionEdit Set(BrandStyleGuideSectionKey key, string body) =>
        new(key, null, body);

    private static BrandStyleGuideSectionEdit SetVariant(string channelKey, string body) =>
        new(BrandStyleGuideSectionKey.ChannelVariant, channelKey, body);

    private static BrandStyleGuideSectionEdit Clear(BrandStyleGuideSectionKey key) => new(key, null, null);

    private static BrandStyleGuideRuleServiceModel Rule(BrandStyleGuideRuleKind kind, string text) =>
        new(kind, text);

    private static BrandStyleGuideSourceServiceModel Source(Guid documentId, int versionNumber) =>
        new(documentId, versionNumber);

    /// <summary>
    /// What the composed edit carries for a cleared section, whatever the request wrote: the view-model layer
    /// resolves blank text to null before anything is applied, so the applier sees one shape for "no text".
    /// </summary>
    private static string? Blank(string? body) => string.IsNullOrWhiteSpace(body) ? null : body;
}
