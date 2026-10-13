namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The four columns of the content board, in the order a card travels through them (13.4, KAN-001).
/// </summary>
/// <remarks>
/// Stored as its integer, so members are appended and never renumbered, and the number is the column's place on
/// the board: ordering by it is ordering left to right. Zero is not a column, so a card whose column was never
/// set is refused by the store rather than read as <see cref="ToDo"/>.
/// <para>
/// <strong>Where a card is in the creator's work, not where a post is in a provider's.</strong>
/// <see cref="Approved"/> means the creator approved the work; whether anything was delivered is a
/// <c>Publication</c> record's to say (content.md, publishing.md).
/// </para>
/// </remarks>
public enum ContentCardColumn
{
    ToDo = 1,

    InProcess = 2,

    ReadyForReview = 3,

    Approved = 4,
}
