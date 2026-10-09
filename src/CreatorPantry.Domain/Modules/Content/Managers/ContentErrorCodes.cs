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

    /// <summary>
    /// The prompt is not well formed: no text, text past the column's length, an unknown or retired channel
    /// key, a kind or source outside the enum, or provenance that disagrees with itself — a manual prompt
    /// naming a template, a generated one naming none. Carries field errors.
    /// </summary>
    public const string PromptInvalid = "content.prompt.invalid";

    public const string PromptForbidden = "content.prompt.forbidden";

    /// <summary>
    /// The prompt is well formed but pins a recipe, a version or an AI proposal this workspace does not have.
    /// </summary>
    /// <remarks>
    /// 422 rather than 400 by the suffix rule, and that is the right reading: the request is shaped correctly
    /// and the server simply cannot accept what it names. Another workspace's id gets this same answer, with
    /// the same wording, so a prompt save cannot be used to ask what a neighbour owns. Field errors name which
    /// pin failed.
    /// </remarks>
    public const string PromptLineageUnprocessable = "content.prompt.lineage.unprocessable";

    /// <summary>
    /// The insert collided and nothing was written. Unlike the lineage refusal this one can end differently on
    /// a retry, which is why it is a conflict rather than a 422.
    /// </summary>
    public const string PromptConflict = "content.prompt.conflict";

    /// <summary>
    /// No prompt in this workspace has that id.
    /// </summary>
    /// <remarks>
    /// <strong>Also the answer for another workspace's prompt</strong>, in the same words and with the same
    /// body, which is how a library stays undisclosed to its neighbours — the global query filter makes the two
    /// cases genuinely one rather than merely reported as one. Carries no field errors: the id came from the
    /// route, so there is no field to name, and a <c>ValidationProblemDetails</c> would publish an empty
    /// <c>errors</c> object for nothing.
    /// </remarks>
    public const string PromptNotFound = "content.prompt.not_found";

    /// <summary>The prompt-library query cannot be run as described. Carries field errors.</summary>
    public const string PromptSearchInvalid = "content.prompt.search.invalid";

    /// <summary>
    /// The cursor was issued for a different workspace or a different set of filters.
    /// </summary>
    /// <remarks>
    /// Its own code rather than folded into <see cref="PromptSearchInvalid"/>, so a paging client can tell "your
    /// filters are wrong" from "start the list again" — the only one of the two it can act on automatically.
    /// </remarks>
    public const string PromptCursorInvalid = "content.prompt.cursor.invalid";

    /// <summary>The creative context failed shape validation. Carries field errors.</summary>
    public const string CreativeContextInvalid = "content.creative_context.invalid";

    /// <summary>The caller's role may read creative contexts but not change them.</summary>
    public const string CreativeContextForbidden = "content.creative_context.forbidden";

    /// <summary>
    /// No such creative context, or no such reference on it, in the resolved workspace.
    /// </summary>
    /// <remarks>
    /// One code for an unknown id and for another workspace's, because the query filter means the server never
    /// sees the other row: by the time the answer is null those were never two conditions here.
    /// </remarks>
    public const string CreativeContextNotFound = "content.creative_context.not_found";

    /// <summary>
    /// The context has changed since the read this edit was composed against.
    /// </summary>
    /// <remarks>
    /// Nothing was written. The creator's attempted change is theirs to keep: the client re-reads, shows both,
    /// and sends again with the new token.
    /// </remarks>
    public const string CreativeContextStale = "content.creative_context.stale.conflict";

    /// <summary>
    /// A new context could not be written just then and nothing was stored. Worth retrying as it stands.
    /// </summary>
    public const string CreativeContextNotSaved = "content.creative_context.conflict";

    /// <summary>
    /// The reference names something this workspace cannot point new work at.
    /// </summary>
    /// <remarks>
    /// Deliberately one code and one sentence for a target that does not exist, one that belongs to another
    /// workspace, and one that is archived, deleted, declined or expired. Telling them apart would let an id be
    /// used to ask what a neighbour owns.
    /// </remarks>
    public const string CreativeContextReferenceUnprocessable = "content.creative_context.reference.unprocessable";

    /// <summary>The context already names that source. A source is named once.</summary>
    public const string CreativeContextReferenceDuplicate = "content.creative_context.reference.duplicate.conflict";

    /// <summary>The context already holds as many references as one piece of work may.</summary>
    public const string CreativeContextReferenceLimit = "content.creative_context.reference_limit.unprocessable";

    /// <summary>A channel key names no channel, or newly chooses one that has been retired.</summary>
    public const string CreativeContextChannelUnprocessable = "content.creative_context.channel.unprocessable";

    /// <summary>The theme key is not a theme of this workspace, or newly chooses a retired one.</summary>
    public const string CreativeContextThemeUnprocessable = "content.creative_context.theme.unprocessable";

    /// <summary>The cursor was not issued for this workspace's list. Start again without one.</summary>
    public const string CreativeContextCursorInvalid = "content.creative_context.cursor.invalid";
}
