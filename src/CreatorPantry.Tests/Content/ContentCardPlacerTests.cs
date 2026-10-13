using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// 13.6b's placement arithmetic, with no store: where a card goes in a column, when no other card has to
/// change, and when the column has to be re-spaced.
/// </summary>
public sealed class ContentCardPlacerTests
{
    private const long Gap = ContentCardPolicy.RankGap;

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid Moving = Guid.NewGuid();

    private static IReadOnlyList<ContentCardSlot> Column(params (Guid Id, long Rank)[] slots) =>
        [.. slots.Select(slot => new ContentCardSlot(slot.Id, slot.Rank))];

    /// <summary>The column as it would read after the placement, by id.</summary>
    private static List<Guid> After(IReadOnlyList<ContentCardSlot> column, ContentCardPlacement placement)
    {
        var ranks = column
            .Where(slot => slot.CardId != Moving)
            .ToDictionary(slot => slot.CardId, slot => placement.Respaced.TryGetValue(slot.CardId, out var rank) ? rank : slot.SortRank);
        ranks[Moving] = placement.SortRank;

        return [.. ranks.OrderBy(pair => pair.Value).Select(pair => pair.Key)];
    }

    [Fact]
    public void An_empty_column_starts_one_gap_in()
    {
        var placement = ContentCardPlacer.Place(Column(), Moving, beforeCardId: null);

        Assert.Equal(ContentCardPlacementKind.Place, placement.Kind);
        Assert.Equal(Gap, placement.SortRank);
        Assert.Empty(placement.Respaced);
    }

    [Fact]
    public void With_no_card_named_it_goes_one_gap_below_the_last()
    {
        var column = Column((A, 100), (B, 5000));

        var placement = ContentCardPlacer.Place(column, Moving, beforeCardId: null);

        Assert.Equal(5000 + Gap, placement.SortRank);
        Assert.Equal([A, B, Moving], After(column, placement));
    }

    [Fact]
    public void Before_the_first_card_it_goes_one_gap_above_it_even_below_zero()
    {
        var column = Column((A, 10), (B, 5000));

        var placement = ContentCardPlacer.Place(column, Moving, A);

        Assert.Equal(10 - Gap, placement.SortRank);
        Assert.Equal([Moving, A, B], After(column, placement));
        Assert.Empty(placement.Respaced);
    }

    [Fact]
    public void Between_two_cards_it_takes_the_midpoint_and_no_other_card_changes()
    {
        var column = Column((A, 1024), (B, 2048), (C, 3072));

        var placement = ContentCardPlacer.Place(column, Moving, B);

        Assert.Equal(ContentCardPlacementKind.Place, placement.Kind);
        Assert.Equal(1536, placement.SortRank);
        Assert.Empty(placement.Respaced);
        Assert.Equal([A, Moving, B, C], After(column, placement));
    }

    [Theory]
    [InlineData(1024, 1026, 1025)]
    [InlineData(-10, 10, 0)]
    [InlineData(0, 7, 3)]
    public void A_midpoint_is_strictly_between_its_neighbours(long above, long below, long expected)
    {
        var placement = ContentCardPlacer.Place(Column((A, above), (B, below)), Moving, B);

        Assert.Equal(ContentCardPlacementKind.Place, placement.Kind);
        Assert.Equal(expected, placement.SortRank);
        Assert.InRange(placement.SortRank, above + 1, below - 1);
    }

    [Theory]
    [InlineData(1024, 1025)]
    [InlineData(1024, 1024)]
    public void Neighbours_with_no_whole_number_between_them_re_space_the_column_in_its_order(long above, long below)
    {
        // Two cards that share a rank are in this order because the store put them in it; that order is kept.
        var column = Column((A, above), (B, below), (C, 9000));

        var placement = ContentCardPlacer.Place(column, Moving, B);

        Assert.Equal(ContentCardPlacementKind.Rebalance, placement.Kind);
        Assert.Equal(
            new Dictionary<Guid, long> { [A] = Gap, [B] = 3 * Gap, [C] = 4 * Gap },
            placement.Respaced);
        Assert.Equal(2 * Gap, placement.SortRank);
        Assert.Equal([A, Moving, B, C], After(column, placement));
    }

    [Fact]
    public void A_card_already_in_the_column_is_placed_among_the_others()
    {
        var column = Column((A, 1024), (Moving, 2048), (B, 3072), (C, 4096));

        // Down two places: before C.
        var down = ContentCardPlacer.Place(column, Moving, C);
        Assert.Equal(3584, down.SortRank);
        Assert.Equal([A, B, Moving, C], After(column, down));

        // To the top, and to the bottom.
        Assert.Equal([Moving, A, B, C], After(column, ContentCardPlacer.Place(column, Moving, A)));
        Assert.Equal([A, B, C, Moving], After(column, ContentCardPlacer.Place(column, Moving, beforeCardId: null)));
    }

    [Fact]
    public void A_card_that_is_already_exactly_there_is_unchanged()
    {
        var column = Column((A, 1024), (Moving, 2048), (B, 3072));

        // Before the card it is already before, and — as the last card — at the bottom.
        Assert.Equal(ContentCardPlacementKind.Unchanged, ContentCardPlacer.Place(column, Moving, B).Kind);
        Assert.Equal(
            ContentCardPlacementKind.Unchanged,
            ContentCardPlacer.Place(Column((A, 1024), (Moving, 2048)), Moving, beforeCardId: null).Kind);

        // The only card in its column has nowhere else to be.
        Assert.Equal(ContentCardPlacementKind.Unchanged, ContentCardPlacer.Place(Column((Moving, 1)), Moving, null).Kind);
    }

    [Fact]
    public void A_place_among_cards_that_share_its_rank_is_judged_by_position_not_by_rank()
    {
        // All three share one rank; the store's order is A, Moving, B.
        var column = Column((A, 1024), (Moving, 1024), (B, 1024));

        // Already between them: nothing to do, though no rank says so.
        Assert.Equal(ContentCardPlacementKind.Unchanged, ContentCardPlacer.Place(column, Moving, B).Kind);

        // To the top is a real change, and the two it leaves behind keep their order.
        var top = ContentCardPlacer.Place(column, Moving, A);
        Assert.Equal([Moving, A, B], After(column, top));

        // To the bottom is one gap past the last, which is past both.
        var bottom = ContentCardPlacer.Place(column, Moving, beforeCardId: null);
        Assert.Equal(1024 + Gap, bottom.SortRank);
    }

    [Fact]
    public void A_card_that_is_not_in_the_column_cannot_be_gone_before_and_neither_can_the_card_itself()
    {
        var column = Column((A, 1024), (Moving, 2048));

        Assert.Equal(ContentCardPlacementKind.TargetNotInColumn, ContentCardPlacer.Place(column, Moving, Guid.NewGuid()).Kind);
        Assert.Equal(ContentCardPlacementKind.TargetNotInColumn, ContentCardPlacer.Place(column, Moving, Moving).Kind);
        Assert.Equal(ContentCardPlacementKind.TargetNotInColumn, ContentCardPlacer.Place(Column(), Moving, A).Kind);
    }

    [Fact]
    public void A_re_spacing_is_only_written_over_the_column_it_was_planned_from()
    {
        var plannedFrom = Column((A, 1024), (Moving, 1500), (B, 1025), (C, 9000));
        ContentCardSlot[] same = [new(C, 9000), new(A, 1024), new(B, 1025)];

        // The same cards at the same ranks, in whatever order they were loaded; the moving card is not one.
        Assert.True(ContentCardPlacer.StillAsPlanned(plannedFrom, Moving, same));

        // Somebody gave B a new place between the plan and the write. Writing the plan would undo it.
        Assert.False(ContentCardPlacer.StillAsPlanned(plannedFrom, Moving, [new(C, 9000), new(A, 1024), new(B, 5000)]));

        // A card left the column, and a card arrived in it.
        Assert.False(ContentCardPlacer.StillAsPlanned(plannedFrom, Moving, [new(A, 1024), new(B, 1025)]));
        Assert.False(ContentCardPlacer.StillAsPlanned(plannedFrom, Moving, [.. same, new(Guid.NewGuid(), 1)]));

        // One left and another arrived: the same number of cards is not the same cards.
        Assert.False(ContentCardPlacer.StillAsPlanned(plannedFrom, Moving, [new(A, 1024), new(B, 1025), new(Guid.NewGuid(), 9000)]));
    }

    [Fact]
    public void The_same_column_and_request_always_resolve_the_same_way()
    {
        var column = Column((A, 7), (B, 8), (C, 20_000));

        var first = ContentCardPlacer.Place(column, Moving, B);
        var second = ContentCardPlacer.Place(column, Moving, B);

        Assert.Equal(first.Kind, second.Kind);
        Assert.Equal(first.SortRank, second.SortRank);
        Assert.Equal(first.Respaced, second.Respaced);
    }
}
