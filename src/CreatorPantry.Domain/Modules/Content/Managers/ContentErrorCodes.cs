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

    /// <summary>The cursor was not issued for this theme's history. Start again without one.</summary>
    public const string ThemedDayHistoryCursorInvalid = "content.weekly_theme.history.cursor.invalid";

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

    /// <summary>
    /// No such card in this workspace — which is also the answer for a card another workspace owns, so a card id
    /// cannot be used to ask what a neighbour's board holds. Carries no field errors: the id came from the route.
    /// </summary>
    public const string CardNotFound = "content.card.not_found";

    /// <summary>The card failed shape validation, or names a channel or a cooking code that is not offered. Carries field errors.</summary>
    public const string CardInvalid = "content.card.invalid";

    /// <summary>The caller's role may read the board but not change it.</summary>
    public const string CardForbidden = "content.card.forbidden";

    /// <summary>
    /// A record the card points at cannot be linked: it is not in this workspace, or can no longer be used.
    /// One answer for both, so a card cannot be used to ask what a neighbour owns. Carries field errors.
    /// </summary>
    public const string CardLineageUnprocessable = "content.card.lineage.unprocessable";

    /// <summary>
    /// The card has changed since the read this edit was composed against. Nothing was written: keep what the
    /// creator typed, read the card again, and send the edit again.
    /// </summary>
    public const string CardStale = "content.card.stale.conflict";

    /// <summary>
    /// The card named as the place to move to is not in the target column of this board — it does not exist,
    /// is in another column, is the card being moved, or belongs to another workspace. One answer for all of
    /// them. Carries field errors.
    /// </summary>
    public const string CardMoveUnprocessable = "content.card.move.unprocessable";

    /// <summary>
    /// The card's linked picture could not be removed as asked — its token is out of date, or it cannot be
    /// removed just now. Nothing was deleted, the card included: read the picture again and decide again.
    /// </summary>
    public const string CardLinkedAssetConflict = "content.card.linked_asset.conflict";

    /// <summary>
    /// A record the package pins cannot be pinned: it is not in this workspace, can no longer be used, or is
    /// not the approved version. Nothing was created. Carries field errors, and never tells a record that
    /// belongs to another workspace from one that does not exist.
    /// </summary>
    public const string CardPackageUnprocessable = "content.card.package.unprocessable";

    /// <summary>
    /// A Content Pipeline run cannot be put on the board yet: it has no picture saved to the library. Nothing
    /// was created. Carries a field error naming <c>keepers</c>.
    /// </summary>
    public const string CardPipelineUnprocessable = "content.card.pipeline.unprocessable";

    /// <summary>The card could not be written just then and nothing was. Safe to try again.</summary>
    public const string CardNotSaved = "content.card.conflict";

    /// <summary>The board query cannot be run as described. Carries field errors.</summary>
    public const string CardListInvalid = "content.card.list.invalid";

    /// <summary>
    /// The cursor was issued for a different workspace or a different set of filters. Its own code, for
    /// <see cref="PromptCursorInvalid"/>'s reason: it is the one refusal a paging client can act on by itself.
    /// </summary>
    public const string CardCursorInvalid = "content.card.cursor.invalid";

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

    /// <summary>The post body or its limit result is not well formed. Carries field errors.</summary>
    public const string SocialInvalid = "content.social.invalid";

    /// <summary>The caller's role may read posts but not make this move.</summary>
    public const string SocialForbidden = "content.social.forbidden";

    /// <summary>
    /// No such creative context, or no post for that channel on it, in the resolved workspace.
    /// </summary>
    /// <remarks>One code for an unknown id and for another workspace's, as everywhere else in this module.</remarks>
    public const string SocialNotFound = "content.social.not_found";

    /// <summary>
    /// The channel has a newer revision, or has moved, since the read this was composed against. Nothing was
    /// written; re-read and try again.
    /// </summary>
    public const string SocialStale = "content.social.stale.conflict";

    /// <summary>The channel is not in a state this decision applies to — nothing awaits one, or it needs reaffirming.</summary>
    public const string SocialDecisionConflict = "content.social.decision.conflict";

    /// <summary>
    /// The newest revision was written against a recipe version that is no longer the latest, so it cannot be
    /// accepted as current. Edit or regenerate it.
    /// </summary>
    public const string SocialSourceStale = "content.social.source_stale.conflict";

    /// <summary>A channel key names no channel, or newly chooses one that has been retired.</summary>
    public const string SocialChannelUnprocessable = "content.social.channel.unprocessable";

    /// <summary>
    /// The revision names a source — a recipe version, a brand revision, an AI proposal — that this workspace
    /// cannot pin it to. One code and one sentence whichever it was, so a write cannot be used to ask what a
    /// neighbour owns.
    /// </summary>
    public const string SocialSourceUnprocessable = "content.social.source.unprocessable";

    /// <summary>
    /// A long-form draft was asked for a creative context this workspace does not have (LF.2.2).
    /// </summary>
    /// <remarks>
    /// One code and one sentence for a context that does not exist and one that is another workspace's, so the
    /// request cannot be used to ask what a neighbour holds.
    /// </remarks>
    public const string LongFormContextUnprocessable = "content.long_form.context.unprocessable";

    /// <summary>No such draft in this workspace — or one in another, which is the same answer.</summary>
    public const string LongFormDraftNotFound = "content.long_form.draft.not_found";

    /// <summary>The draft moved since the read this write was composed against.</summary>
    public const string LongFormDraftStale = "content.long_form.draft.stale.conflict";

    /// <summary>A plan edit or approval for a draft that has no plan yet.</summary>
    public const string LongFormPlanNotStarted = "content.long_form.plan.not_started.conflict";

    /// <summary>A plan edit, approval or new outline for a draft whose sections are already being written.</summary>
    public const string LongFormPlanLocked = "content.long_form.plan.locked.conflict";

    /// <summary>A submitted plan is not one that could be kept: its shape, not its readiness.</summary>
    public const string LongFormPlanInvalid = "content.long_form.plan.invalid";

    /// <summary>
    /// Approval of a plan that does not meet the long-form shape rules. The field errors name each section.
    /// </summary>
    public const string LongFormPlanUnprocessable = "content.long_form.plan.unprocessable";

    /// <summary>A generated plan names a source this workspace cannot pin it to. One code, whichever it was.</summary>
    public const string LongFormSourceUnprocessable = "content.long_form.source.unprocessable";

    public const string LongFormCursorInvalid = "content.long_form.cursor.invalid";

    /// <summary>A section was asked for on a draft whose plan the creator has not approved (LF.3.1).</summary>
    public const string LongFormPlanNotApproved = "content.long_form.plan.not_approved.conflict";

    /// <summary>A section was asked for on a draft that has been accepted and is the article of record.</summary>
    public const string LongFormDraftAccepted = "content.long_form.draft.accepted.conflict";

    /// <summary>A section was asked for against a revision that is no longer the draft's latest.</summary>
    public const string LongFormRevisionStale = "content.long_form.revision.stale.conflict";

    /// <summary>The section named is not in the revision named.</summary>
    public const string LongFormSectionNotFound = "content.long_form.section.not_found";

    /// <summary>
    /// A section was asked for, edited or settled on a draft that is neither approved nor being written (LF.3.3).
    /// </summary>
    public const string LongFormDraftNotWritable = "content.long_form.draft.not_writable.conflict";

    /// <summary>
    /// A generated body was not put in its section, because the creator changed or settled that section after
    /// asking for it. The proposal is kept; the section is as the creator left it.
    /// </summary>
    public const string LongFormSectionSuperseded = "content.long_form.section.superseded.conflict";

    /// <summary>A section with no text was asked to be marked settled.</summary>
    public const string LongFormSectionNotWritten = "content.long_form.section.not_written.conflict";

    /// <summary>A settled section was asked to be written again. Clear the mark first.</summary>
    public const string LongFormSectionSettled = "content.long_form.section.settled.conflict";

    /// <summary>A section edit or settle request is not one that could be kept: its shape.</summary>
    public const string LongFormSectionInvalid = "content.long_form.section.invalid";

    /// <summary>An acceptance request is not one that could be read: its shape (LF.3.4).</summary>
    public const string LongFormAcceptanceInvalid = "content.long_form.acceptance.invalid";

    /// <summary>
    /// The document has something wrong with it that the acceptance did not confirm. The <c>issues</c> extension
    /// lists everything wrong, with the sections concerned; nothing was accepted.
    /// </summary>
    public const string LongFormAcceptanceConfirmationRequired =
        "content.long_form.acceptance.confirmation_required.unprocessable";

    /// <summary>No section of the piece has any text, so there is no document to accept.</summary>
    public const string LongFormAcceptanceNothingWritten = "content.long_form.acceptance.nothing_written.unprocessable";

    /// <summary>An item order or a framing was sent for a piece that is not a newsletter issue (LF.4.1).</summary>
    public const string LongFormNotAnIssue = "content.long_form.draft.not_an_issue.conflict";

    /// <summary>
    /// An item order is not one that could be kept: its shape, or it does not name each of the issue's items
    /// exactly once.
    /// </summary>
    public const string LongFormItemOrderInvalid = "content.long_form.item_order.invalid";

    /// <summary>A subject line or note is not one that could be kept: its shape or its length.</summary>
    public const string LongFormFramingInvalid = "content.long_form.framing.invalid";

    /// <summary>An export was asked for in a format there is none of (LF.5.1).</summary>
    public const string LongFormExportInvalid = "content.long_form.export.invalid";

    /// <summary>
    /// An export was asked for of a draft whose creator has not accepted it. Only the article of record leaves.
    /// </summary>
    public const string LongFormExportNotAccepted = "content.long_form.export.not_accepted.conflict";

    /// <summary>The accepted revision has no written section, so there is no document to export.</summary>
    public const string LongFormExportNothingWritten = "content.long_form.export.nothing_written.unprocessable";
}
