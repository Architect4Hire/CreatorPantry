namespace CreatorPantry.Domain.Modules.Content.Managers;

public static class ContentErrorCodes
{
    public const string RevisionNotFound = "content.revision.not_found";

    /// <summary>The submitted week is not a well-formed week. Carries field errors.</summary>
    public const string WeeklyThemesInvalid = "content.weekly_themes.invalid";

    public const string WeeklyThemesForbidden = "content.weekly_themes.forbidden";

    /// <summary>
    /// No theme in this workspace has that key. Also the answer for another workspace's key, which is how a
    /// workspace's themes stay undisclosed.
    /// </summary>
    public const string WeeklyThemeNotFound = "content.weekly_theme.not_found";

    /// <summary>
    /// Two replaces raced and this one lost: reload the week and apply the change again. Raised from the unique
    /// indexes, which are the authority on one-theme-per-day and one-row-per-key.
    /// </summary>
    public const string WeeklyThemesConflict = "content.weekly_themes.conflict";

    /// <summary>
    /// The workspace already holds as many theme rows as it may, live and retired together. Well formed, so a
    /// 422 rather than a 400; the remedy is to delete a retired theme.
    /// </summary>
    public const string WeeklyThemesLimit = "content.weekly_themes.limit.unprocessable";

    /// <summary>
    /// The seed request is not usable: a malformed token, a day that is not a day, or a pinned facet naming
    /// something no catalogue has or that is no longer offered. Carries field errors.
    /// </summary>
    public const string ContentSeedInvalid = "content.seed.invalid";
}
