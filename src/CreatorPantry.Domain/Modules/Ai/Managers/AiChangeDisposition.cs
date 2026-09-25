namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What the creator decided about one proposed change.</summary>
/// <remarks>
/// Per change rather than per proposal, because accepting a selection is the ordinary case. The operation's
/// own status records the overall outcome; this records which suggestions were actually taken, so a
/// partially-accepted proposal can still show a creator what they had already declined.
/// </remarks>
public enum AiChangeDisposition
{
    /// <summary>Not yet decided. The state every change is written in.</summary>
    Pending = 0,

    /// <summary>The creator took this change. It was applied to a new recipe version.</summary>
    Accepted = 1,

    /// <summary>The creator declined this change. Recorded rather than discarded.</summary>
    Rejected = 2,
}
