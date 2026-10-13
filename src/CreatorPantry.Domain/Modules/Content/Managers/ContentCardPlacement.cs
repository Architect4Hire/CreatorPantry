namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>One card's place in a column, as the store orders it: its id and its rank.</summary>
public sealed record ContentCardSlot(Guid CardId, long SortRank);

/// <summary>How a placement was resolved.</summary>
public enum ContentCardPlacementKind
{
    /// <summary>The card takes <see cref="ContentCardPlacement.SortRank"/>; no other card changes.</summary>
    Place = 1,

    /// <summary>The card is already exactly there. Nothing is to be written.</summary>
    Unchanged = 2,

    /// <summary>
    /// There is no rank left between the two neighbours, so the whole column is re-spaced:
    /// <see cref="ContentCardPlacement.Respaced"/> gives every other card's new rank.
    /// </summary>
    Rebalance = 3,

    /// <summary>The card named as the target is not in the column. Nothing is to be written.</summary>
    TargetNotInColumn = 4,
}

/// <param name="SortRank">The moving card's new rank. Zero when nothing is to be written.</param>
/// <param name="Respaced">For a rebalance, the new rank of every other card in the column; otherwise empty.</param>
public sealed record ContentCardPlacement(
    ContentCardPlacementKind Kind, long SortRank, IReadOnlyDictionary<Guid, long> Respaced)
{
    private static readonly IReadOnlyDictionary<Guid, long> NoOthers = new Dictionary<Guid, long>();

    internal static ContentCardPlacement At(long sortRank) => new(ContentCardPlacementKind.Place, sortRank, NoOthers);

    internal static ContentCardPlacement Of(ContentCardPlacementKind kind) => new(kind, 0, NoOthers);
}

/// <summary>
/// Where a moved card goes, as arithmetic on a column's ranks (13.6b). Deterministic and free of I/O: the same
/// column and the same request always resolve the same way.
/// </summary>
/// <remarks>
/// <para>
/// The column is handed in <strong>in the store's own order</strong> — rank, then id — and is never re-sorted
/// here. Two cards may share a rank, and only the store knows which way it breaks that tie; ordering ids in
/// memory would not agree with it.
/// </para>
/// <para>
/// A card dropped between two others takes the midpoint of their ranks, which touches no other card. When the
/// two neighbours are equal or adjacent there is no whole number between them, and the column is re-spaced to
/// <see cref="ContentCardPolicy.RankGap"/> in its current order with the card in its new place.
/// </para>
/// </remarks>
public static class ContentCardPlacer
{
    /// <param name="column">Every card in the target column, in the store's order. May include the moving card.</param>
    /// <param name="movingCardId">The card being placed.</param>
    /// <param name="beforeCardId">The card it goes immediately before, or null for the bottom of the column.</param>
    public static ContentCardPlacement Place(IReadOnlyList<ContentCardSlot> column, Guid movingCardId, Guid? beforeCardId)
    {
        ArgumentNullException.ThrowIfNull(column);

        // A card cannot go before itself: there is no such place.
        if (beforeCardId == movingCardId)
        {
            return ContentCardPlacement.Of(ContentCardPlacementKind.TargetNotInColumn);
        }

        var current = IndexOf(column, movingCardId);
        var others = current < 0 ? column : [.. column.Where(slot => slot.CardId != movingCardId)];

        var target = beforeCardId is { } before ? IndexOf(others, before) : others.Count;
        if (target < 0)
        {
            return ContentCardPlacement.Of(ContentCardPlacementKind.TargetNotInColumn);
        }

        // Already in this column and already at that place among the others: nothing to do.
        if (current == target)
        {
            return ContentCardPlacement.Of(ContentCardPlacementKind.Unchanged);
        }

        long? above = target > 0 ? others[target - 1].SortRank : null;
        long? below = target < others.Count ? others[target].SortRank : null;

        return (above, below) switch
        {
            (null, null) => ContentCardPlacement.At(ContentCardPolicy.RankGap),
            (null, { } first) => ContentCardPlacement.At(first - ContentCardPolicy.RankGap),
            ({ } last, null) => ContentCardPlacement.At(last + ContentCardPolicy.RankGap),
            ({ } upper, { } lower) when lower - upper >= 2 => ContentCardPlacement.At(upper + ((lower - upper) / 2)),
            _ => Respace(others, target),
        };
    }

    /// <summary>
    /// Whether the cards about to be re-spaced are still exactly the cards the plan was made from: the same
    /// ones, each at the rank it was planned around.
    /// </summary>
    /// <remarks>
    /// A re-spacing is planned from one read of the column and written through a second, later read that
    /// carries the row versions. A card that was moved between the two would arrive with its new rank and a
    /// fresh version, and writing the planned rank over it would undo that move without any conflict being
    /// raised. So the second read is compared with the first, and any difference — a card gone, a card arrived,
    /// a rank changed — means the plan is out of date and must not be written.
    /// </remarks>
    /// <param name="plannedFrom">The column the placement was computed from. May include the moving card.</param>
    /// <param name="movingCardId">The card being placed, which is not one of the cards re-spaced.</param>
    /// <param name="loaded">The other cards of the column as just loaded for writing.</param>
    public static bool StillAsPlanned(
        IReadOnlyList<ContentCardSlot> plannedFrom, Guid movingCardId, IReadOnlyCollection<ContentCardSlot> loaded)
    {
        ArgumentNullException.ThrowIfNull(plannedFrom);
        ArgumentNullException.ThrowIfNull(loaded);

        var planned = plannedFrom
            .Where(slot => slot.CardId != movingCardId)
            .ToDictionary(slot => slot.CardId, slot => slot.SortRank);

        return loaded.Count == planned.Count
            && loaded.All(slot => planned.TryGetValue(slot.CardId, out var rank) && rank == slot.SortRank);
    }

    /// <summary>Every card one gap apart, in the order given, with a gap left at <paramref name="target"/> for the moving card.</summary>
    private static ContentCardPlacement Respace(IReadOnlyList<ContentCardSlot> others, int target)
    {
        var respaced = new Dictionary<Guid, long>(others.Count);

        for (var index = 0; index < others.Count; index++)
        {
            var place = index < target ? index : index + 1;
            respaced[others[index].CardId] = (place + 1) * ContentCardPolicy.RankGap;
        }

        return new ContentCardPlacement(ContentCardPlacementKind.Rebalance, (target + 1) * ContentCardPolicy.RankGap, respaced);
    }

    private static int IndexOf(IReadOnlyList<ContentCardSlot> column, Guid cardId)
    {
        for (var index = 0; index < column.Count; index++)
        {
            if (column[index].CardId == cardId)
            {
                return index;
            }
        }

        return -1;
    }
}
