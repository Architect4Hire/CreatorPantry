namespace CreatorPantry.Domain.Modules.Content.Managers;

public static class ContentAuditActions
{
    /// <summary>
    /// The week as a whole, because that is what one replace changes. The resource id is the workspace's own.
    /// </summary>
    public const string WeeklyThemesResourceType = "WorkspaceWeeklyThemes";

    /// <summary>One theme, for the one operation that acts on a single row.</summary>
    public const string WeeklyThemeResourceType = "WorkspaceWeeklyTheme";

    /// <summary>
    /// The creator rewrote their week: any mix of themes added, reworded, moved, retired and brought back.
    /// </summary>
    /// <remarks>
    /// The before and after references are counts of the live week, never a key or a name — a theme's name is
    /// the creator's own words, and an audit summary is not where free text about private content belongs. A
    /// replace that changed nothing writes no row: there is nothing to audit about a week that still says what
    /// it said.
    /// </remarks>
    public const string WeeklyThemesReplaced = "content.weekly_themes.replaced";

    /// <summary>
    /// An Owner deleted one theme outright, rather than retiring it by leaving it out of the week.
    /// </summary>
    /// <remarks>
    /// Audited separately and at a higher bar because it is the one irreversible move here: retirement keeps the
    /// row so a record that stored the key still resolves, and this does not. The reference is the row's id,
    /// which outlives the row and is the only thing left to name it by.
    /// </remarks>
    public const string WeeklyThemeDeleted = "content.weekly_theme.deleted";

    public const string CreativeContextResourceType = "CreativeContext";

    public const string CreativeContextCreated = "content.creative_context.created";

    /// <summary>
    /// A context left the recent list. Audited because it is the one change here that takes work out of view;
    /// ordinary edits are autosaved many times a minute and would bury it.
    /// </summary>
    public const string CreativeContextArchived = "content.creative_context.archived";

    public const string CreativeContextRestored = "content.creative_context.restored";

    /// <summary>One channel of a post package. The resource id is the channel slot's own.</summary>
    public const string SocialChannelResourceType = "SocialPackageChannel";

    /// <summary>
    /// A creator's decision about one channel. Drafting — a regeneration, an edit — is not audited: it is the
    /// work itself, it changes nothing anyone else relies on, and every revision is already kept.
    /// </summary>
    /// <remarks>
    /// The before and after references are statuses and the summary names the channel key and a revision
    /// number — never a word of the post, which is private creator content.
    /// </remarks>
    public const string SocialChannelAccepted = "content.social_channel.accepted";

    /// <inheritdoc cref="SocialChannelAccepted"/>
    public const string SocialChannelRejected = "content.social_channel.rejected";

    /// <inheritdoc cref="SocialChannelAccepted"/>
    public const string SocialChannelReaffirmed = "content.social_channel.reaffirmed";

    /// <summary>
    /// The system found an accepted post out of step with its recipe. No actor: no role may declare content
    /// stale by hand.
    /// </summary>
    public const string SocialChannelMarkedStale = "content.social_channel.marked_stale";
}
