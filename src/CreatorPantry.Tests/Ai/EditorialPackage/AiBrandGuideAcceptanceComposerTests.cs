using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// 11A.18's reassembly of a stored brand-guide proposal: which rows make an item, which items become guidance,
/// which produce nothing, and where the creator's own words go.
/// </summary>
/// <remarks>
/// <para>
/// Unit tests over a pure function, so the cases that matter can be stated directly — including the ones a
/// model would have to misbehave to produce, which no integration test will reach.
/// </para>
/// <para>
/// This is also where the two sides of the flattening are held against each other. The handler writes the rows
/// and the composer reads them, the compiler cannot make them agree, and the way they would fail if they
/// stopped agreeing is silent: items that read as malformed and are dropped.
/// </para>
/// </remarks>
public sealed class AiBrandGuideAcceptanceComposerTests
{
    // ---- reading the rows back -------------------------------------------------------------------------

    [Fact]
    public void An_items_metadata_rows_are_read_back_onto_the_row_that_carries_its_text()
    {
        var rows = new ProposalRows();
        var item = rows.Section("voice", "A friend who cooks.", citations: [PassageA]);

        var read = Assert.Single(AiBrandGuideAcceptanceComposer.Items(rows.Changes));

        Assert.Equal(item, read.ItemId);
        Assert.Equal(AiBrandGuideItemKinds.Section, read.ItemKind);
        Assert.Equal(AiBrandGuideDimension.Voice, read.Dimension);
        Assert.Equal("A friend who cooks.", read.Text);
        Assert.Equal([PassageA], read.CitedPassageIds);

        // Every row but the text row, which is what gets accepted alongside the item.
        Assert.Equal(4, read.CompanionIds.Count);
    }

    /// <summary>
    /// Metadata with no text row is orphaned from whatever it described. Left out rather than invented into an
    /// item with no words.
    /// </summary>
    [Fact]
    public void A_target_with_no_text_row_is_not_an_item()
    {
        var rows = new ProposalRows();
        var orphan = Guid.NewGuid();
        rows.Set(orphan, AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Section);
        rows.Set(orphan, AiBrandGuideFields.Dimension, "voice");

        Assert.Empty(AiBrandGuideAcceptanceComposer.Items(rows.Changes));
    }

    [Fact]
    public void Items_come_back_in_the_order_the_handler_wrote_them()
    {
        var rows = new ProposalRows();
        var first = rows.Section("voice", "First.");
        var second = rows.Rule(AiBrandGuideRuleKind.Do, "Second.");
        var third = rows.Finding(AiBrandGuideItemKinds.Uncertainty, "Third.");

        Assert.Equal(
            [first, second, third],
            AiBrandGuideAcceptanceComposer.Items(rows.Changes).Select(item => item.ItemId));
    }

    // ---- what becomes guidance -------------------------------------------------------------------------

    [Theory]
    [InlineData("voice", BrandStyleGuideSectionKey.Voice)]
    [InlineData("tone", BrandStyleGuideSectionKey.Tone)]
    [InlineData("tenor", BrandStyleGuideSectionKey.Tenor)]
    [InlineData("style", BrandStyleGuideSectionKey.WritingStyle)]
    [InlineData("language", BrandStyleGuideSectionKey.Vocabulary)]
    [InlineData("blog", BrandStyleGuideSectionKey.BlogGuidance)]
    [InlineData("social", BrandStyleGuideSectionKey.SocialGuidance)]
    [InlineData("visual", BrandStyleGuideSectionKey.VisualIdentity)]
    public void Each_dimension_writes_the_section_it_maps_to(string dimension, BrandStyleGuideSectionKey expected)
    {
        var rows = new ProposalRows();
        rows.Section(dimension, "Guidance.");

        var composed = ComposeAll(rows);

        var section = Assert.Single(composed.Sections);
        Assert.Equal(expected, section.SectionKey);
        Assert.Null(section.ChannelKey);
        Assert.Equal("Guidance.", section.Body);
    }

    [Fact]
    public void A_channel_dimension_writes_a_variant_carrying_its_channel()
    {
        var rows = new ProposalRows();
        rows.Section("channel", "Short, and lead with the picture.", channelKey: "instagram");

        var section = Assert.Single(ComposeAll(rows).Sections);

        Assert.Equal(BrandStyleGuideSectionKey.ChannelVariant, section.SectionKey);
        Assert.Equal("instagram", section.ChannelKey);
    }

    [Theory]
    [InlineData(AiBrandGuideRuleKind.Do, BrandStyleGuideRuleKind.Do)]
    [InlineData(AiBrandGuideRuleKind.Dont, BrandStyleGuideRuleKind.Dont)]
    public void A_rule_keeps_the_direction_it_pointed(
        AiBrandGuideRuleKind proposed, BrandStyleGuideRuleKind expected)
    {
        var rows = new ProposalRows();
        rows.Rule(proposed, "Say it plainly.");

        var rule = Assert.Single(ComposeAll(rows).Rules);

        Assert.Equal(expected, rule.Kind);
        Assert.Equal("Say it plainly.", rule.Text);
    }

    /// <summary>
    /// Every dimension a proposal may return has somewhere to go. A new one added without a section key would
    /// be offered to a creator, accepted, and silently dropped — this is what makes that a failing test instead.
    /// </summary>
    [Fact]
    public void Every_dimension_a_proposal_can_return_maps_to_a_section_key()
    {
        var unmapped = Enum.GetValues<AiBrandGuideDimension>()
            .Where(dimension => dimension is not AiBrandGuideDimension.Unspecified)
            .Where(dimension => AiBrandGuideAcceptanceComposer.SectionKeyFor(dimension) is null)
            .ToList();

        Assert.Empty(unmapped);
    }

    // ---- what produces nothing -------------------------------------------------------------------------

    [Theory]
    [InlineData(AiBrandGuideItemKinds.Conflict)]
    [InlineData(AiBrandGuideItemKinds.Uncertainty)]
    public void A_finding_produces_no_guidance_and_is_reported_dropped(string itemKind)
    {
        var rows = new ProposalRows();
        var item = rows.Finding(itemKind, "Two of your documents disagree.");

        var composed = ComposeAll(rows);

        Assert.Empty(composed.Sections);
        Assert.Empty(composed.Rules);
        Assert.Equal([item], composed.DroppedItemIds);
    }

    /// <summary>
    /// A section written under the wrong key is worse than one that did not arrive: the creator would have to
    /// find it to know. So nothing is guessed.
    /// </summary>
    [Fact]
    public void A_section_whose_dimension_is_not_stated_is_dropped_rather_than_placed()
    {
        var rows = new ProposalRows();
        var targetId = Guid.NewGuid();
        var item = rows.Add(targetId, "Guidance with no dimension.");
        rows.Set(targetId, AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Section);

        var composed = ComposeAll(rows);

        Assert.Empty(composed.Sections);
        Assert.Equal([item], composed.DroppedItemIds);
    }

    [Fact]
    public void A_dimension_this_build_does_not_know_is_dropped()
    {
        var rows = new ProposalRows();
        var item = rows.Section("sonnet", "Guidance for a dimension that does not exist.");

        var composed = ComposeAll(rows);

        Assert.Empty(composed.Sections);
        Assert.Equal([item], composed.DroppedItemIds);
    }

    /// <summary>
    /// A channel variant is told apart by its channel, so one with no channel has no identity — and the stored
    /// unique index would refuse a second of them anyway.
    /// </summary>
    [Fact]
    public void A_channel_section_with_no_channel_is_dropped()
    {
        var rows = new ProposalRows();
        var item = rows.Section("channel", "Short.", channelKey: null);

        var composed = ComposeAll(rows);

        Assert.Empty(composed.Sections);
        Assert.Equal([item], composed.DroppedItemIds);
    }

    [Fact]
    public void A_rule_that_does_not_say_which_way_it_points_is_dropped()
    {
        var rows = new ProposalRows();
        var targetId = Guid.NewGuid();
        var item = rows.Add(targetId, "Something about contractions.");
        rows.Set(targetId, AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Rule);

        var composed = ComposeAll(rows);

        Assert.Empty(composed.Rules);
        Assert.Equal([item], composed.DroppedItemIds);
    }

    [Fact]
    public void An_item_that_was_not_accepted_contributes_nothing_and_is_not_dropped()
    {
        var rows = new ProposalRows();
        rows.Section("voice", "Not taken.");
        var taken = rows.Section("tone", "Taken.");

        var items = AiBrandGuideAcceptanceComposer.Items(rows.Changes);
        var composed = AiBrandGuideAcceptanceComposer.Compose(items, new HashSet<Guid> { taken }, Edits());

        Assert.Single(composed.Sections);
        Assert.Empty(composed.DroppedItemIds);
    }

    // ---- citations -------------------------------------------------------------------------------------

    [Fact]
    public void Citations_are_collected_across_the_accepted_items_and_deduplicated()
    {
        var rows = new ProposalRows();
        rows.Section("voice", "One.", citations: [PassageA, PassageB]);
        rows.Section("tone", "Two.", citations: [PassageB]);

        var composed = ComposeAll(rows);

        Assert.Equal([PassageA, PassageB], composed.CitedPassageIds);
    }

    /// <summary>A dropped item's citations do not reach the version: its guidance did not either.</summary>
    [Fact]
    public void A_dropped_items_citations_are_not_collected()
    {
        var rows = new ProposalRows();
        rows.Finding(AiBrandGuideItemKinds.Conflict, "They disagree.", citations: [PassageA]);

        Assert.Empty(ComposeAll(rows).CitedPassageIds);
    }

    /// <summary>
    /// A malformed citation is skipped rather than failing the acceptance: it is provenance, and refusing a
    /// creator's whole decision over a footnote would cost them the guidance. What is never done is inventing
    /// an id in its place.
    /// </summary>
    [Fact]
    public void A_malformed_citation_is_skipped_and_the_rest_survive()
    {
        var rows = new ProposalRows();
        var targetId = Guid.NewGuid();
        rows.Add(targetId, "Guidance.");
        rows.Set(targetId, AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Section);
        rows.Set(targetId, AiBrandGuideFields.Dimension, "voice");
        rows.Set(targetId, AiBrandGuideFields.Citations, $"not-a-guid,{PassageA:N}");

        Assert.Equal([PassageA], ComposeAll(rows).CitedPassageIds);
    }

    // ---- the creator's own words -----------------------------------------------------------------------

    [Fact]
    public void A_rewrite_replaces_the_models_words_and_is_reported()
    {
        var rows = new ProposalRows();
        var item = rows.Section("tone", "Warm, but never fussy.");

        var items = AiBrandGuideAcceptanceComposer.Items(rows.Changes);
        var composed = AiBrandGuideAcceptanceComposer.Compose(
            items, new HashSet<Guid> { item }, Edits((item, "Dry, and a bit wry.")));

        Assert.Equal("Dry, and a bit wry.", Assert.Single(composed.Sections).Body);
        Assert.Equal([item], composed.RewrittenItemIds);
    }

    /// <summary>
    /// A rewrite of something that becomes no guidance is not a rewrite that happened: the creator's words did
    /// not reach the guide, so counting it as one would overstate what landed. The acceptance seam refuses this
    /// case outright; the composer still has to be honest about it.
    /// </summary>
    [Fact]
    public void A_rewrite_of_a_dropped_item_is_counted_as_dropped_rather_than_rewritten()
    {
        var rows = new ProposalRows();
        var item = rows.Finding(AiBrandGuideItemKinds.Conflict, "They disagree.");

        var items = AiBrandGuideAcceptanceComposer.Items(rows.Changes);
        var composed = AiBrandGuideAcceptanceComposer.Compose(
            items, new HashSet<Guid> { item }, Edits((item, "My words.")));

        Assert.Empty(composed.RewrittenItemIds);
        Assert.Equal([item], composed.DroppedItemIds);
    }

    [Theory]
    [InlineData(AiBrandGuideItemKinds.Section, true)]
    [InlineData(AiBrandGuideItemKinds.Rule, true)]
    [InlineData(AiBrandGuideItemKinds.Conflict, false)]
    [InlineData(AiBrandGuideItemKinds.Uncertainty, false)]
    public void Only_guidance_may_be_rewritten(string itemKind, bool editable)
    {
        var rows = new ProposalRows();
        var targetId = Guid.NewGuid();
        rows.Add(targetId, "Text.");
        rows.Set(targetId, AiBrandGuideFields.ItemKind, itemKind);

        var item = Assert.Single(AiBrandGuideAcceptanceComposer.Items(rows.Changes));

        Assert.Equal(editable, AiBrandGuideAcceptanceComposer.CanEdit(item));
    }

    [Fact]
    public void An_item_whose_kind_is_not_stated_may_not_be_rewritten()
    {
        var rows = new ProposalRows();
        rows.Add(Guid.NewGuid(), "Text with no kind.");

        var item = Assert.Single(AiBrandGuideAcceptanceComposer.Items(rows.Changes));

        Assert.False(AiBrandGuideAcceptanceComposer.CanEdit(item));
    }

    // ---- the boundary that has not moved ---------------------------------------------------------------

    /// <summary>
    /// 11A.18 gives a proposed brand-guide row a path to a guide version. It gives it no path to a recipe, and
    /// these two omissions are what guarantee that — asserted here so the guarantee is a failing test rather
    /// than a paragraph.
    /// </summary>
    [Fact]
    public void A_proposed_brand_guide_row_still_has_no_path_to_a_recipe()
    {
        Assert.Null(AiChangeTargetPolicy.For(AiChangeTargetKind.BrandGuideSection));
        Assert.Empty(AiChangeApplicability.For(AiChangeTargetKind.BrandGuideSection));

        Assert.All(
            Enum.GetValues<AiChangeKind>(),
            kind => Assert.False(
                AiChangeApplicability.IsApplicable(kind, AiChangeTargetKind.BrandGuideSection)));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static readonly Guid PassageA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PassageB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static AiBrandGuideComposition ComposeAll(ProposalRows rows)
    {
        var items = AiBrandGuideAcceptanceComposer.Items(rows.Changes);

        return AiBrandGuideAcceptanceComposer.Compose(
            items, items.Select(item => item.ItemId).ToHashSet(), Edits());
    }

    private static Dictionary<Guid, string> Edits(params (Guid ItemId, string Value)[] edits) =>
        edits.ToDictionary(edit => edit.ItemId, edit => edit.Value);

    /// <summary>
    /// Builds rows in the shape <c>BrandGuideProposalAiTaskHandler.Translate</c> writes them: one <c>Add</c> per
    /// item carrying its text, with its metadata as <c>Set</c> rows against the same target id.
    /// </summary>
    private sealed class ProposalRows
    {
        private readonly List<AiStructuredChange> _changes = [];
        private int _order;

        public IReadOnlyList<AiStructuredChange> Changes => _changes;

        public Guid Add(Guid targetId, string text) =>
            Row(AiChangeKind.Add, targetId, null, text).Id;

        public void Set(Guid targetId, string field, string? value) =>
            Row(AiChangeKind.Set, targetId, field, value);

        public Guid Section(
            string dimension,
            string body,
            string? channelKey = null,
            IReadOnlyList<Guid>? citations = null)
        {
            var targetId = Guid.NewGuid();
            var itemId = Add(targetId, body);

            Set(targetId, AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Section);
            Set(targetId, AiBrandGuideFields.Dimension, dimension);
            Set(targetId, AiBrandGuideFields.ChannelKey, channelKey);
            Set(targetId, AiBrandGuideFields.Evidence, AiBrandGuideEvidence.Sources.ToString());
            Set(targetId, AiBrandGuideFields.Citations, Cited(citations));

            return itemId;
        }

        public Guid Rule(AiBrandGuideRuleKind kind, string text)
        {
            var targetId = Guid.NewGuid();
            var itemId = Add(targetId, text);

            Set(targetId, AiBrandGuideFields.ItemKind, AiBrandGuideItemKinds.Rule);
            Set(targetId, AiBrandGuideFields.RuleKind, kind.ToString());
            Set(targetId, AiBrandGuideFields.Evidence, AiBrandGuideEvidence.Questionnaire.ToString());

            return itemId;
        }

        public Guid Finding(string itemKind, string summary, IReadOnlyList<Guid>? citations = null)
        {
            var targetId = Guid.NewGuid();
            var itemId = Add(targetId, summary);

            Set(targetId, AiBrandGuideFields.ItemKind, itemKind);
            Set(targetId, AiBrandGuideFields.Dimension, "style");
            Set(targetId, AiBrandGuideFields.Citations, Cited(citations));

            return itemId;
        }

        private static string? Cited(IReadOnlyList<Guid>? citations) =>
            citations is null or { Count: 0 }
                ? null
                : string.Join(',', citations.Select(passageId => passageId.ToString("N")));

        /// <remarks>
        /// An empty value writes no row, exactly as the handler's own <c>AddSet</c> skips one — otherwise these
        /// fixtures would exercise a shape the handler never produces.
        /// </remarks>
        private AiStructuredChange Row(AiChangeKind kind, Guid targetId, string? field, string? value)
        {
            var change = new AiStructuredChange
            {
                Id = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                AiProposalId = Guid.NewGuid(),
                ChangeKind = kind,
                TargetKind = AiChangeTargetKind.BrandGuideSection,
                TargetId = targetId,
                FieldName = field,
                AfterValue = value,
                SortOrder = _order++,
            };

            if (kind is AiChangeKind.Add || !string.IsNullOrEmpty(value))
            {
                _changes.Add(change);
            }

            return change;
        }
    }
}
