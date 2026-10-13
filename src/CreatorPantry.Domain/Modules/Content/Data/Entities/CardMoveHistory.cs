using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One time a card entered a column: when it was created, and every move to a different column after that
/// (13.4, KAN-005). Interior to <see cref="ContentCard"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Write-once.</strong> <c>ImmutableRecordInterceptor</c> refuses every update and delete, so what the
/// board says happened cannot be revised afterwards. The rows leave only with their card, by the database's own
/// cascade — the write seam deletes a card without loading these.
/// </para>
/// <para>
/// <strong>A reorder is not a move.</strong> A row needs a different column on each side, which the store
/// enforces: changing a card's place within its column writes nothing here, so the history never claims a
/// transition that did not happen.
/// </para>
/// <para>
/// <strong><see cref="Sequence"/> is the order.</strong> It counts from one — the creation row, the only one
/// with no <see cref="FromColumn"/> — and is unique per card, so two moves in the same instant still read in
/// one order, and two moves composed against the same state cannot both be written. A card's move count is its
/// highest sequence less one.
/// </para>
/// </remarks>
public class CardMoveHistory : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ContentCardId { get; set; }

    public int Sequence { get; set; }

    /// <summary>The column the card left, or null for the row that records its creation.</summary>
    public ContentCardColumn? FromColumn { get; set; }

    public ContentCardColumn ToColumn { get; set; }

    /// <summary>The creator's own note on the move. Private content: never logged.</summary>
    public string? Note { get; set; }

    /// <summary>Who moved it, or null for a move no member made. Not a foreign key, as elsewhere.</summary>
    public Guid? ActorMembershipId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
