namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// How one post body measured against its channel's writing profile. Stored as its number; append, never
/// renumber.
/// </summary>
/// <remarks>
/// A result, not a rule: the limit and the counting belong to the channel profile (content.md), and this row
/// only records what that profile said about this body when it was written. An over-limit body is stored as
/// written and flagged — never trimmed.
/// </remarks>
public enum SocialLimitStatus
{
    /// <summary>No profile measured this body. It carries no count, no limit and no profile version.</summary>
    NotChecked = 0,

    /// <summary>Measured, and inside the limit — or the profile sets none.</summary>
    Within = 1,

    /// <summary>Measured, and longer than the channel allows.</summary>
    Over = 2,
}
