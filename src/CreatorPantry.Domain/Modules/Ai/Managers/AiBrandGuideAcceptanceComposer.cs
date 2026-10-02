using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// One item of a brand-guide proposal, reassembled from the rows it was flattened into.
/// </summary>
/// <param name="ItemId">The <see cref="AiChangeKind.Add"/> row carrying the item's text. The id a creator selects.</param>
/// <param name="CompanionIds">
/// The <see cref="AiChangeKind.Set"/> rows describing it. Accepted or rejected with the item, never on their own.
/// </param>
/// <param name="ItemKind">
/// <c>section</c>, <c>rule</c>, <c>conflict</c> or <c>uncertainty</c>, or null when the rows do not say.
/// </param>
internal sealed record AiBrandGuideProposedItem(
    Guid ItemId,
    IReadOnlyList<Guid> CompanionIds,
    string? ItemKind,
    AiBrandGuideDimension? Dimension,
    string? ChannelKey,
    AiBrandGuideRuleKind? RuleKind,
    IReadOnlyList<Guid> CitedPassageIds,
    string Text);

/// <summary>
/// The guidance an acceptance will write, and what became of each accepted item.
/// </summary>
/// <param name="Sections">
/// The accepted sections, in proposal order. May contain two entries for one section key — the caller refuses
/// that rather than letting one quietly overwrite the other.
/// </param>
/// <param name="CitedPassageIds">
/// Every passage the accepted guidance cites, deduplicated. Turned into source-document versions by the brand
/// module, which is the only place that mapping lives.
/// </param>
/// <param name="RewrittenItemIds">Accepted items whose text the creator replaced with their own.</param>
/// <param name="DroppedItemIds">
/// Accepted items that produce no guidance: a conflict, an uncertainty, or a row too malformed to place.
/// </param>
internal sealed record AiBrandGuideComposition(
    IReadOnlyList<BrandStyleGuideSectionServiceModel> Sections,
    IReadOnlyList<BrandStyleGuideRuleServiceModel> Rules,
    IReadOnlyList<Guid> CitedPassageIds,
    IReadOnlySet<Guid> RewrittenItemIds,
    IReadOnlySet<Guid> DroppedItemIds);

/// <summary>
/// Turns the accepted part of a stored brand-guide proposal, plus the creator's own rewrites, into the guidance
/// the brand module will write (11A.18).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The inverse of <see cref="BrandGuideProposalAiTaskHandler"/>'s translation, and it relies on the same
/// convention</strong>: one <see cref="AiChangeKind.Add"/> row per item carrying its text, with its item kind,
/// dimension, channel, evidence basis and citations as <see cref="AiChangeKind.Set"/> rows sharing its target
/// id. The two have to agree, and the compiler cannot make them — which is why both sides name
/// <see cref="AiBrandGuideFields"/> and <see cref="AiBrandGuideItemKinds"/> rather than string literals, and why
/// a round-trip test holds one against the other.
/// </para>
/// <para>
/// <strong>Two of the four item kinds produce nothing, by design.</strong> A conflict reports two pieces of the
/// creator's own material that disagree and an uncertainty reports something the answer could not settle; both
/// are findings about the evidence, and a guide has no field for either. They remain selectable — an accept-all
/// has to be able to name every item or it is not a confirmation — and they are reported as dropped, so a
/// creator who ticked one learns it did not land instead of wondering where it went.
/// </para>
/// <para>
/// <strong>Nothing is repaired and nothing is guessed.</strong> An item whose rows do not say what kind it is,
/// a dimension this build does not know, a channel section with no channel, or a rule with no kind is dropped
/// rather than placed somewhere plausible: a section written under the wrong key is worse than one that did not
/// arrive, because the creator would have to find it to know.
/// </para>
/// <para>
/// Pure, and it makes no database call: it reads stored rows and the creator's own words. That is what lets the
/// caller compose before opening a transaction, so nothing it does has to be rolled back.
/// </para>
/// </remarks>
internal static class AiBrandGuideAcceptanceComposer
{
    /// <summary>
    /// Which guide section one proposal dimension writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A translation, not a shared enum</strong>, for the reason <see cref="AiBrandGuideDimension"/>'s
    /// own remarks give: a proposal's vocabulary is not a stored guide's, and binding them together would make a
    /// model's answer shape the guide. The two are deliberately different sizes — the guide has keys no
    /// proposal produces, and they stay the creator's to write.
    /// </para>
    /// <para>
    /// <strong><see cref="AiBrandGuideDimension.Visual"/> maps to the umbrella key.</strong> The guide splits
    /// visual direction four ways — identity, photography, image prompts, and what the brand's imagery avoids —
    /// and the proposal has one dimension spanning the first three. It is written to
    /// <see cref="BrandStyleGuideSectionKey.VisualIdentity"/> because that is the general one; splitting a
    /// single body across three keys would be inventing distinctions the answer did not make.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<AiBrandGuideDimension, BrandStyleGuideSectionKey> SectionKeys = new()
    {
        [AiBrandGuideDimension.Voice] = BrandStyleGuideSectionKey.Voice,
        [AiBrandGuideDimension.Tone] = BrandStyleGuideSectionKey.Tone,
        [AiBrandGuideDimension.Tenor] = BrandStyleGuideSectionKey.Tenor,
        [AiBrandGuideDimension.Style] = BrandStyleGuideSectionKey.WritingStyle,
        [AiBrandGuideDimension.Language] = BrandStyleGuideSectionKey.Vocabulary,
        [AiBrandGuideDimension.Channel] = BrandStyleGuideSectionKey.ChannelVariant,
        [AiBrandGuideDimension.Blog] = BrandStyleGuideSectionKey.BlogGuidance,
        [AiBrandGuideDimension.Social] = BrandStyleGuideSectionKey.SocialGuidance,
        [AiBrandGuideDimension.Visual] = BrandStyleGuideSectionKey.VisualIdentity,
    };

    /// <summary>Which guide section a dimension writes, or null when this build cannot place it.</summary>
    public static BrandStyleGuideSectionKey? SectionKeyFor(AiBrandGuideDimension dimension) =>
        SectionKeys.TryGetValue(dimension, out var key) ? key : null;

    /// <summary>Every dimension that can become a guide section, for a test that holds the two enums together.</summary>
    public static IReadOnlyCollection<AiBrandGuideDimension> MappedDimensions => SectionKeys.Keys;

    /// <summary>
    /// The proposal's items, in the order the handler wrote them.
    /// </summary>
    /// <remarks>
    /// Grouped by target id, which is how the flattening ties an item's metadata to its text. The
    /// <see cref="AiChangeKind.Add"/> row is the item; a group with none is metadata orphaned from whatever it
    /// described, and is left out rather than invented into an item with no words.
    /// </remarks>
    public static IReadOnlyList<AiBrandGuideProposedItem> Items(IEnumerable<AiStructuredChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var items = new List<(int SortOrder, AiBrandGuideProposedItem Item)>();

        foreach (var group in changes
            .Where(change => change.TargetKind is AiChangeTargetKind.BrandGuideSection && change.TargetId is not null)
            .GroupBy(change => change.TargetId!.Value))
        {
            var ordered = group.OrderBy(change => change.SortOrder).ToList();
            var text = ordered.FirstOrDefault(change => change.ChangeKind is AiChangeKind.Add);

            if (text is null)
            {
                continue;
            }

            string? Field(string name) => ordered
                .FirstOrDefault(change => change.ChangeKind is AiChangeKind.Set && change.FieldName == name)
                ?.AfterValue;

            items.Add((text.SortOrder, new AiBrandGuideProposedItem(
                text.Id,
                [.. ordered.Where(change => change.Id != text.Id).Select(change => change.Id)],
                Field(AiBrandGuideFields.ItemKind),
                AiBrandGuideDimensionCatalog.Parse(Field(AiBrandGuideFields.Dimension)),
                Field(AiBrandGuideFields.ChannelKey),
                Enum.TryParse<AiBrandGuideRuleKind>(Field(AiBrandGuideFields.RuleKind), out var kind)
                    && kind is not AiBrandGuideRuleKind.Unspecified
                        ? kind
                        : null,
                Citations(Field(AiBrandGuideFields.Citations)),
                text.AfterValue ?? string.Empty)));
        }

        // Back into the handler's own order, so the items appear in the order the creator read them rather than
        // in whatever order the target ids happened to group.
        return [.. items.OrderBy(entry => entry.SortOrder).Select(entry => entry.Item)];
    }

    /// <summary>
    /// Whether this item's text is something a creator may rewrite.
    /// </summary>
    /// <remarks>
    /// Only a section or a rule: those become guidance, so replacing their words changes what the guide says. A
    /// conflict and an uncertainty become nothing, and accepting a rewrite of one would take the creator's words
    /// and discard them while reporting success.
    /// </remarks>
    public static bool CanEdit(AiBrandGuideProposedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.ItemKind is AiBrandGuideItemKinds.Section or AiBrandGuideItemKinds.Rule;
    }

    /// <summary>
    /// The guidance the accepted items make, with the creator's rewrites in place of the model's words.
    /// </summary>
    /// <param name="items">Every item of the proposal, as <see cref="Items"/> read them.</param>
    /// <param name="accepted">The item ids the creator accepted.</param>
    /// <param name="edits">The creator's own text, by item id.</param>
    public static AiBrandGuideComposition Compose(
        IReadOnlyList<AiBrandGuideProposedItem> items,
        IReadOnlySet<Guid> accepted,
        IReadOnlyDictionary<Guid, string> edits)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(edits);

        var sections = new List<BrandStyleGuideSectionServiceModel>();
        var rules = new List<BrandStyleGuideRuleServiceModel>();
        var citations = new List<Guid>();
        var cited = new HashSet<Guid>();
        var rewritten = new HashSet<Guid>();
        var dropped = new HashSet<Guid>();

        foreach (var item in items.Where(item => accepted.Contains(item.ItemId)))
        {
            var text = edits.TryGetValue(item.ItemId, out var written) ? written : item.Text;

            if (edits.ContainsKey(item.ItemId))
            {
                rewritten.Add(item.ItemId);
            }

            var placed = item.ItemKind switch
            {
                AiBrandGuideItemKinds.Section => Section(item, text, sections),
                AiBrandGuideItemKinds.Rule => Rule(item, text, rules),

                // A conflict, an uncertainty, or a row whose kind the proposal does not state. None of them is
                // guidance, and a guide has nowhere to put any of them.
                _ => false,
            };

            if (!placed)
            {
                dropped.Add(item.ItemId);

                // A dropped item cites nothing into the version, and its rewrite went nowhere: counted as
                // dropped rather than as rewritten, because the creator's words did not reach the guide.
                rewritten.Remove(item.ItemId);
                continue;
            }

            foreach (var passageId in item.CitedPassageIds)
            {
                if (cited.Add(passageId))
                {
                    citations.Add(passageId);
                }
            }
        }

        return new AiBrandGuideComposition(sections, rules, citations, rewritten, dropped);
    }

    /// <summary>
    /// Adds one accepted section, or answers false when it cannot be placed.
    /// </summary>
    /// <remarks>
    /// A dimension this build does not map and a channel variant with no channel both answer false. The second
    /// is the one worth naming: <see cref="BrandStyleGuideSectionKey.ChannelVariant"/> is the only key a version
    /// may hold more than once, and it is told apart by its channel — so a variant with no channel has no
    /// identity, and the stored unique index would refuse a second one of them anyway.
    /// </remarks>
    private static bool Section(
        AiBrandGuideProposedItem item, string text, List<BrandStyleGuideSectionServiceModel> sections)
    {
        if (item.Dimension is not { } dimension || SectionKeyFor(dimension) is not { } key)
        {
            return false;
        }

        var channel = key is BrandStyleGuideSectionKey.ChannelVariant ? item.ChannelKey : null;

        if (key is BrandStyleGuideSectionKey.ChannelVariant && string.IsNullOrWhiteSpace(channel))
        {
            return false;
        }

        sections.Add(new BrandStyleGuideSectionServiceModel(key, channel, text));

        return true;
    }

    /// <summary>Adds one accepted rule, or answers false when the proposal did not say which way it points.</summary>
    private static bool Rule(
        AiBrandGuideProposedItem item, string text, List<BrandStyleGuideRuleServiceModel> rules)
    {
        if (item.RuleKind is not { } kind)
        {
            return false;
        }

        rules.Add(new BrandStyleGuideRuleServiceModel(
            kind is AiBrandGuideRuleKind.Do ? BrandStyleGuideRuleKind.Do : BrandStyleGuideRuleKind.Dont, text));

        return true;
    }

    /// <summary>
    /// The passage ids one item cites, as the handler joined them.
    /// </summary>
    /// <remarks>
    /// An unparseable entry is skipped rather than failing the acceptance: a citation is provenance, the ids
    /// come from rows this server wrote, and refusing a creator's whole decision over one malformed one would
    /// cost them the guidance to protect a footnote. What it must not do is invent an id, which is why nothing
    /// is substituted.
    /// </remarks>
    private static IReadOnlyList<Guid> Citations(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return
        [
            .. value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => Guid.TryParseExact(entry, "N", out var passageId) ? passageId : (Guid?)null)
                .Where(passageId => passageId is not null)
                .Select(passageId => passageId!.Value),
        ];
    }
}
