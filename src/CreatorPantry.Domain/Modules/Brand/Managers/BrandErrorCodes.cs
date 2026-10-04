namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>Stable, machine-readable codes for the brand module. The <c>.not_found</c> suffix maps to 404.</summary>
public static class BrandErrorCodes
{
    /// <summary>
    /// The workspace has no brand profile yet. Distinct from the workspace itself being unknown or
    /// inaccessible, which the tenancy pipeline answers earlier with its own code, so a caller who reaches this
    /// one is already a member and learns nothing about any other workspace.
    /// </summary>
    public const string ProfileNotFound = "brand.profile.not_found";

    /// <summary>The request failed shape or domain validation. Falls through to 400.</summary>
    public const string InvalidRequest = "brand.profile.invalid_request";

    /// <summary>The caller's role is below Editor. Maps to 403.</summary>
    public const string Forbidden = "brand.profile.forbidden";

    /// <summary>The profile moved on since the state the edit was composed against. Maps to 409.</summary>
    public const string Conflict = "brand.profile.conflict";

    /// <summary>A profile already exists for this workspace; edit it instead. Maps to 409.</summary>
    public const string AlreadyExistsConflict = "brand.profile.exists.conflict";

    /// <summary>
    /// Logo links cannot be accepted yet: the server cannot verify an asset id belongs to this workspace until
    /// the media seam exists. Maps to 422.
    /// </summary>
    public const string AssetsUnprocessable = "brand.assets.unprocessable";

    /// <summary>The caller's role is below Editor. Maps to 403.</summary>
    public const string SourceForbidden = "brand.source.forbidden";

    /// <summary>The upload's metadata or file part failed shape validation. Falls through to 400.</summary>
    public const string SourceInvalidRequest = "brand.source.invalid_request";

    /// <summary>
    /// No such source document, or version of one, in the resolved workspace. Maps to 404.
    /// </summary>
    /// <remarks>
    /// One code for five situations that must be indistinguishable: an id that was never issued, another
    /// workspace's document, a document the workspace removed, a version number the document does not have,
    /// and a version number it does not have <em>yet</em>. A caller who could tell these apart could use a
    /// read to learn that a document exists somewhere they cannot see it, or to count a workspace's versions
    /// (tenancy.md).
    /// </remarks>
    public const string SourceNotFound = "brand.source.not_found";

    /// <summary>
    /// The document moved on since the read the replacement was composed against. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Says only that the caller is holding a stale picture, never what changed or who changed it: the remedy
    /// is to read the document again, and a client told more would learn about edits it may not be entitled to
    /// a history of.
    /// </remarks>
    public const string SourceConflict = "brand.source.conflict";

    /// <summary>
    /// The document is archived, so it cannot take a new version. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SourceConflict"/> because the remedy is different and a client can act on it:
    /// restore the document, then replace its file. Deliberately not a 404 — archiving is a shelf, a document
    /// on it still reads and still downloads, so pretending it does not exist would be a worse answer than
    /// refusing the write.
    /// </remarks>
    public const string SourceArchivedConflict = "brand.source.archived.conflict";

    /// <summary>The file is larger than its format allows. Maps to 413.</summary>
    public const string SourceFileTooLarge = "brand.source.file.payload_too_large";

    /// <summary>
    /// The bytes are not one of the accepted formats, or are not the format the filename claims. Maps to 422.
    /// </summary>
    public const string SourceFileUnsupported = "brand.source.file.unsupported.unprocessable";

    /// <summary>The bytes start like an accepted format but do not hold together as one. Maps to 422.</summary>
    public const string SourceFileCorrupt = "brand.source.file.corrupt.unprocessable";

    /// <summary>The malware scan refused the file. Maps to 422, and says nothing of what was recognised.</summary>
    public const string SourceFileRejected = "brand.source.file.rejected.unprocessable";

    /// <summary>No scan verdict could be had, so the file was not accepted. Maps to 503.</summary>
    public const string SourceScanUnavailable = "brand.source.scan.unavailable";

    /// <summary>Private storage could not be reached, so nothing was recorded. Maps to 503.</summary>
    public const string SourceStorageUnavailable = "brand.source.storage.unavailable";

    /// <summary>
    /// The version's text has not been extracted yet, so there is nothing to correct. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Not a 404: the version exists, downloads, and reads as <c>NotExtracted</c> through the review route. And
    /// not an invitation to write the first artifact either — a correction at ordinal 1 would race the worker
    /// that is about to write it, and the loser of that race is whichever one the creator cares about.
    /// </remarks>
    public const string SourceExtractionPendingConflict = "brand.source.extraction.pending.conflict";

    /// <summary>
    /// The extracted text moved on since the read this correction was composed against. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Says only that the caller is holding a stale copy, never what changed: the remedy is to read the text
    /// again, and a client told more would learn about a collaborator's edits it may not be entitled to a history
    /// of. Nothing is written, and the creator's attempted text comes back to them in their own client rather
    /// than being merged into something they have not seen.
    /// </remarks>
    public const string SourceExtractionConflict = "brand.source.extraction.conflict";

    /// <summary>
    /// The version is not the document's current one, so its text cannot be corrected. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SourceExtractionConflict"/> because the remedy is different and a client can act
    /// on it: correct the current version instead. The restriction exists because only the current version's text
    /// has consumers — chunking, retrieval and guide drafting all read it — so a correction on a superseded
    /// version would be a write whose effect the creator could never see. Reading an older version's text stays
    /// allowed; history is readable.
    /// </remarks>
    public const string SourceExtractionSupersededConflict = "brand.source.extraction.superseded.conflict";

    /// <summary>
    /// Reading the version again would not help: it was already read, it holds nothing to read, or the creator has
    /// corrected it. A conflict with the artifact's state rather than a fault, so the remedy is stated in the message.
    /// </summary>
    public const string SourceExtractionNotRetryableConflict = "brand.source.extraction.not_retryable.conflict";

    /// <summary>The caller's role is below Editor, so they cannot create a brand style guide. Maps to 403.</summary>
    public const string GuideForbidden = "brand.guide.forbidden";

    /// <summary>The guide's name, questionnaire, sections, rules or source list failed validation. Falls through to 400.</summary>
    public const string GuideInvalidRequest = "brand.guide.invalid_request";

    /// <summary>
    /// A selected source document version cannot be used. Maps to 422.
    /// </summary>
    /// <remarks>
    /// One code for every way a pointer can fail to resolve — an id never issued, another workspace's document,
    /// a removed one, a version number the document does not have — so a caller cannot use the refusal to
    /// learn that a document exists somewhere they cannot see it (tenancy.md).
    /// </remarks>
    public const string GuideSourceUnprocessable = "brand.guide.source.unprocessable";

    /// <summary>
    /// No such brand style guide in the resolved workspace. Maps to 404.
    /// </summary>
    /// <remarks>
    /// One code for an id that was never issued and for another workspace's guide, so a read cannot be used to
    /// learn that a guide exists somewhere the caller cannot see it (tenancy.md).
    /// </remarks>
    public const string GuideNotFound = "brand.guide.not_found";

    /// <summary>
    /// The guide exists, but does not have a version number the request named. Maps to 404.
    /// </summary>
    /// <remarks>
    /// Worth telling apart from <see cref="GuideNotFound"/> only because it names the parameter at fault in
    /// <c>errors</c> — a caller who mistyped <c>from</c> is told which side was wrong, and one who mistyped
    /// both is told both rather than being sent round the loop twice. It discloses nothing: the guide is
    /// already known to be readable before any version number is looked up.
    /// </remarks>
    public const string GuideVersionNotFound = "brand.guide.version.not_found";

    /// <summary>
    /// The guide is archived, so none of its versions can be made the workspace default. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Not a 404 — an archived guide reads normally, lists normally and compares normally; archiving is a
    /// shelf, not a deletion. What it cannot do is govern the workspace's writing, and the remedy is one a
    /// client can act on: restore the guide, then activate it.
    /// </remarks>
    public const string GuideArchivedConflict = "brand.guide.archived.conflict";

    /// <summary>
    /// The version has no approval, so it cannot be the workspace default. Maps to 409.
    /// </summary>
    /// <remarks>
    /// A draft is a version nobody has signed off, and the schema agrees: the default's foreign key targets
    /// <c>BrandStyleGuideApprovals</c>, so the database refuses the row even if this check were bypassed.
    /// Refused rather than approved on the way past, because approving and activating are two decisions and
    /// conflating them would let one request make both without saying so.
    /// </remarks>
    public const string GuideVersionUnapprovedConflict = "brand.guide.version.unapproved.conflict";

    /// <summary>
    /// Another request approved the same version at the same moment, so this one wrote nothing. Maps to 409.
    /// </summary>
    /// <remarks>
    /// A narrow race, and only between requests carrying different idempotency keys: the primary key on
    /// <c>BrandStyleGuideApprovals</c> is the version, so one of the two inserts loses. The version is
    /// approved either way, and the remedy is simply to ask again — a retry reads the winner's approval and
    /// answers <c>alreadyApproved</c>. It is a refusal rather than that answer directly because recovering
    /// inside the failed save would discard the idempotency record the attempt staged; see
    /// <c>IBrandStyleGuideDataLayer.ApproveAsync</c>.
    /// </remarks>
    public const string GuideVersionApprovalConflict = "brand.guide.version.approval.conflict";

    /// <summary>
    /// The version cites a source document version that document has since superseded. Maps to 409.
    /// </summary>
    /// <remarks>
    /// The same staleness the version history publishes as <c>staleSourceCount</c>, and the count travels in
    /// the refusal's extensions so a client can say how much is stale without listing the history again. The
    /// remedy is a new version citing the sources as they now stand; there is deliberately no override flag,
    /// because an override is a policy decision rather than a field on a request.
    /// </remarks>
    public const string GuideVersionStaleConflict = "brand.guide.version.stale.conflict";

    /// <summary>
    /// The version has no sections and no rules, so there is nothing for it to govern. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Reachable because creation requires only a display name: a guide may legitimately exist as a name while
    /// its creator is still filling it in. What that version cannot be is the thing every later generation is
    /// grounded on, which would amount to grounding them on nothing.
    /// </remarks>
    public const string GuideVersionEmptyConflict = "brand.guide.version.empty.conflict";

    /// <summary>
    /// The workspace's active version is not the one the request expected. Maps to 409.
    /// </summary>
    /// <remarks>
    /// Unlike this module's other conflicts, this one <strong>names what is actually active</strong>:
    /// <c>activeGuideId</c>, <c>activeVersionId</c> and <c>activeVersionNumber</c> in the problem's
    /// extensions, each explicitly null when the workspace has no default. It discloses nothing a caller could
    /// not already assemble by listing each guide's versions and reading <c>isActive</c>, and without it a
    /// caller whose default lives in another guide has no route to the value this precondition requires.
    /// </remarks>
    public const string GuideActivationConflict = "brand.guide.activation.conflict";

    /// <summary>
    /// The guide's working version is not the one the caller composed against. Maps to 409.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised by the proposal-acceptance write (11A.18), where the caller reviewed guidance explained by one
    /// version of the creator's own answers and the guide has been edited since. Laying the accepted sections
    /// over the newer working version would be a rebase onto content nobody compared them with, and branching
    /// from the older one would discard the edit that happened in between — so neither is done and nothing is
    /// written.
    /// </para>
    /// <para>
    /// Told apart from <see cref="GuideVersionStaleConflict"/>, which is about a version's <em>citations</em>
    /// having been superseded: that one is answered at activation and the remedy is to write a new version;
    /// this one is answered at the moment of writing and the remedy is to ask for the proposal again.
    /// </para>
    /// </remarks>
    public const string GuideWorkingVersionConflict = "brand.guide.workingVersion.conflict";

    /// <summary>
    /// The version a write would produce exceeds one of the guide's own limits. Falls through to 400.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GuideInvalidRequest"/> because the request may be perfectly well formed and
    /// still land here: accepting thirty proposed rules onto a guide that already holds forty exceeds
    /// <see cref="BrandPolicy.MaxStyleGuideRules"/> without any single part of the request being wrong. Refused
    /// rather than truncated — a cap enforced by dropping the tail would discard guidance the creator ticked
    /// and report success.
    /// </remarks>
    public const string GuideVersionLimitExceeded = "brand.guide.version.limit.invalid_request";

    /// <summary>
    /// The caller's own unsaved edit of a guide has moved since the copy they are writing over. Maps to 409.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One creator in two tabs, or an autosave that overlapped a discard: the <c>If-Match</c> quoted is not
    /// the draft's current row version, so the save is refused and the stored draft is untouched. The remedy
    /// is to read the draft again and decide which copy to keep — a client that retried with the same token
    /// could only lose the same race again.
    /// </para>
    /// <para>
    /// <strong>Not <see cref="GuideWorkingVersionConflict"/>, and not a stale draft.</strong> That one is
    /// about the guide having gained a version; this is about the draft itself. A draft composed against an
    /// older version is not refused at all — it is saved and reported as stale, because the creator's own
    /// words are worth more than the tidiness of refusing them.
    /// </para>
    /// </remarks>
    public const string GuideEditSessionConflict = "brand.guide.editSession.conflict";

    // The setup-session codes are underscore-shaped by contract (11A.22), so the `.suffix` rule in
    // ProblemResults.StatusFor cannot map them; each is listed there explicitly.

    /// <summary>The caller's role is below Editor. Maps to 403.</summary>
    public const string SetupSessionForbidden = "brand_setup_session_forbidden";

    /// <summary>The request failed shape validation. Falls through to 400.</summary>
    public const string SetupSessionInvalidRequest = "brand_setup_session_invalid_request";

    /// <summary>The caller has no setup session to complete. Maps to 404.</summary>
    public const string SetupSessionNotFound = "brand_setup_session_not_found";

    /// <summary>
    /// <c>If-Match</c> was missing while a session exists, or does not match it. Maps to 409; the stored
    /// session is untouched.
    /// </summary>
    public const string SetupSessionConflict = "brand_setup_session_conflict";

    /// <summary>The session is already completed and takes no more saves. Maps to 409.</summary>
    public const string SetupSessionCompleted = "brand_setup_session_completed";

    /// <summary>Completing requires furthestStep to be the final step. Maps to 409.</summary>
    public const string SetupSessionNotFinished = "brand_setup_session_not_finished";
}
