namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Limits and ordering constants for the content board, shared by its configuration and its write seam.</summary>
public static class ContentCardPolicy
{
    public const int TitleMaxLength = 200;

    public const int DescriptionMaxLength = 4000;

    /// <summary>The creator's note on why a card moved, kept on the move it explains.</summary>
    public const int MoveNoteMaxLength = 500;

    /// <summary>
    /// A cuisine, dish type or method as the vocabulary codes it. Opaque here: the card stores what the creator
    /// picked and the vocabulary module says what it means. The same width a content seed's keys have.
    /// </summary>
    public const int VocabularyCodeMaxLength = 64;

    /// <summary>An IANA zone identifier such as <c>America/Chicago</c>.</summary>
    public const int TimeZoneIdMaxLength = 64;

    /// <summary>
    /// The distance left between two neighbouring cards' ranks, and the rank of the first card in a column.
    /// </summary>
    /// <remarks>
    /// A card dropped between two others takes the midpoint of their ranks, so a move writes one row. A gap of
    /// 1,024 halves ten times before two neighbours are adjacent; the column is then re-spaced to this gap in
    /// one transaction. Order is <c>Column, SortRank, Id</c>, so two cards that land on the same rank — two
    /// creators dropping into the same slot at once — still read in one order every time.
    /// </remarks>
    public const long RankGap = 1024;

    /// <summary>
    /// How much of a description a list row carries. The whole description is the detail route's.
    /// </summary>
    public const int DescriptionPreviewMaxLength = 200;

    /// <summary>
    /// The most moves the detail route returns, latest first kept. A card moved more often than this says so
    /// rather than returning an unbounded history; its move count is still exact.
    /// </summary>
    public const int DetailHistoryMaxRows = 100;

    /// <summary>
    /// The longest list of allowed review reasons a package stores. Six kinds of reason, the two about posts
    /// once per channel, each with its channel key: twenty channels fit several times over.
    /// </summary>
    public const int AllowedReasonsMaxLength = 4000;

    /// <summary>Caps per package, so one request cannot pin unbounded rows.</summary>
    public const int MaxPackageAssets = 20;

    public const int MaxPackageChannels = 20;
}
