namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// What a rendition is for. Stored as its number; append, never renumber.
/// </summary>
/// <remarks>The box and quality each one means are B-28's (docs/architecture-decisions/ai-fluency.md).</remarks>
public enum MediaRenditionPurpose
{
    /// <summary>Not declared. Never valid on a stored row — the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>The picture at reading size.</summary>
    Web = 1,

    /// <summary>The picture in a grid, a picker or a card.</summary>
    Thumbnail = 2,
}

/// <summary>
/// How an attempt to make a rendition ended. Stored as its number; append, never renumber.
/// </summary>
/// <remarks>
/// Both values are final. There is no pending state because a row is written when the outcome is known,
/// the way a <c>GeneratedImage</c> row is: nothing here ever describes bytes that do not exist yet.
/// </remarks>
public enum MediaRenditionStatus
{
    /// <summary>Not declared. Never valid on a stored row — the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>The rendition's bytes are stored and the row describes them.</summary>
    Ready = 1,

    /// <summary>
    /// No rendition was made and none will be. The source is served as stored.
    /// </summary>
    /// <remarks>
    /// Recorded rather than left as an absence, because an absence looks like work still to do and would be
    /// attempted again on every pass.
    /// </remarks>
    NotCompressed = 2,
}

/// <summary>
/// Why a source has no rendition. Stored as its number; append, never renumber.
/// </summary>
/// <remarks>
/// A code rather than a sentence: it is read by code that decides what to show, and a free-text reason
/// would be a place for a file's own contents to end up in a column.
/// </remarks>
public enum MediaRenditionReason
{
    /// <summary>Not declared. Never valid on a stored row — the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>A format the managed codec does not read: JPEG, WebP or GIF.</summary>
    UnreadableFormat = 1,

    /// <summary>A PNG that does not hold together.</summary>
    Corrupt = 2,

    /// <summary>A valid PNG of a kind out of scope: interlaced, or fewer than eight bits a sample.</summary>
    UnsupportedVariant = 3,

    /// <summary>More bytes, more pixels or a longer edge than a rendition may be made from.</summary>
    TooLarge = 4,

    /// <summary>The picture has transparency a JPEG cannot carry.</summary>
    HasTransparency = 5,

    /// <summary>The rendition came out no smaller than its source, so it was discarded.</summary>
    NotSmaller = 6,

    /// <summary>
    /// Every attempt failed for a reason that might not have been permanent, and the job stopped trying.
    /// </summary>
    /// <remarks>
    /// The one reason that says nothing about the picture. It is what keeps a picture from being retried
    /// for ever, and it is also the only row a later re-queue would be right to remove.
    /// </remarks>
    RetriesExhausted = 7,
}
