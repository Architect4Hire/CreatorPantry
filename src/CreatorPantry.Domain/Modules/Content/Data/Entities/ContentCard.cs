using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One piece of planned work on the workspace's content board (13.4, KAN-001 through KAN-006). Workspace-owned,
/// and the root of the card aggregate: its moves and its recipe package hang off it and go when it goes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A plan, never a delivery.</strong> There is no provider, post id, publication status or delivery
/// time here and none may be added: a card says where the creator's work stands, and <c>Publication</c> records
/// own what a provider did with it (publishing.md). <see cref="ChannelKey"/> is an opaque key into the channel
/// catalogue, not a provider name.
/// </para>
/// <para>
/// <strong>Lineage points; it does not copy.</strong> Every lineage field is optional and names a record the
/// workspace already owns. The card holds no recipe text, prompt text or picture, so nothing here competes
/// with its source as the truth about it (content.md). The keys are workspace-paired, which makes a card naming
/// another workspace's recipe unrepresentable rather than merely refused.
/// </para>
/// <para>
/// <strong>Order is <c>Column, SortRank, Id</c>.</strong> See <see cref="ContentCardPolicy.RankGap"/>. The id
/// is the tiebreak that keeps the order deterministic when two ranks are equal.
/// </para>
/// <para>
/// <strong>Edited in place, guarded by <see cref="RowVersion"/>.</strong> A move and an edit both quote it.
/// What a card *was* is not kept, except for the one thing the board is about — which column it was in, and
/// when — and that is <see cref="CardMoveHistory"/>.
/// </para>
/// </remarks>
public class ContentCard : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The creator's name for the work. Their words, stored as typed.</summary>
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ContentCardColumn Column { get; set; }

    /// <summary>The card's place within <see cref="Column"/>. Lower is higher up the column.</summary>
    public long SortRank { get; set; }

    /// <summary>A <c>ContentChannel.Key</c>, or null for no channel in particular.</summary>
    public string? ChannelKey { get; set; }

    /// <summary>The day the work is planned for, as a calendar date in the workspace's own zone.</summary>
    public DateOnly? PlannedDate { get; set; }

    /// <summary>
    /// The weekday of <see cref="PlannedDate"/>. Derived by Business from the date and never set on its own, so
    /// the two cannot disagree; stored so the board can filter by day without computing one per row.
    /// </summary>
    public DayOfWeek? PlannedDayOfWeek { get; set; }

    /// <summary>A cuisine code from the cooking vocabulary, or null. Descriptive, never a claim.</summary>
    public string? CuisineCode { get; set; }

    /// <inheritdoc cref="CuisineCode"/>
    public string? DishTypeCode { get; set; }

    /// <inheritdoc cref="CuisineCode"/>
    public string? MethodCode { get; set; }

    /// <summary>
    /// How the card came to be here. <see cref="ContentCardOrigin.ContentPipeline"/> marks the one card the
    /// pipeline made for <see cref="CreativeContextId"/>, and is what the store's unique index is filtered on.
    /// </summary>
    /// <remarks>
    /// It follows the link it is about: a pipeline card whose piece of work is changed or unlinked by an edit
    /// becomes an ordinary card, because it is no longer that work's card.
    /// </remarks>
    public ContentCardOrigin Origin { get; set; } = ContentCardOrigin.Board;

    /// <summary>The piece of creative work this card plans, when it came from one.</summary>
    public Guid? CreativeContextId { get; set; }

    public Guid? RecipeId { get; set; }

    /// <summary>A version of <see cref="RecipeId"/>, when the card is about one exact version. Null for the recipe as it stands.</summary>
    public Guid? RecipeVersionId { get; set; }

    public Guid? PromptRecordId { get; set; }

    public Guid? MediaAssetId { get; set; }

    /// <summary>A version of <see cref="MediaAssetId"/>, when the card is about one exact version.</summary>
    public int? MediaAssetVersionNumber { get; set; }

    public Guid? SocialPackageId { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership, as elsewhere.</summary>
    public Guid CreatedByMembershipId { get; set; }

    /// <inheritdoc cref="CreatedByMembershipId"/>
    public Guid UpdatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
