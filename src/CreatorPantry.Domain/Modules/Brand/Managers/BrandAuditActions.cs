namespace CreatorPantry.Domain.Modules.Brand.Managers;

public static class BrandAuditActions
{
    public const string ResourceType = "BrandProfile";

    public const string Created = "brand.profile.created";

    public const string Updated = "brand.profile.updated";

    public const string SourceDocumentResourceType = "BrandSourceDocument";

    public const string SourceDocumentUploaded = "brand.source.uploaded";

    /// <summary>A new version of an existing document's file. Earlier versions are kept and stay readable.</summary>
    public const string SourceDocumentReplaced = "brand.source.replaced";

    /// <summary>Shelved: out of the pickers, still readable, reversible.</summary>
    public const string SourceDocumentArchived = "brand.source.archived";

    public const string SourceDocumentUnarchived = "brand.source.unarchived";

    /// <summary>
    /// Soft-deleted. Nothing is deleted; the row, its versions and its stored objects all survive, and an
    /// Owner can put it back.
    /// </summary>
    public const string SourceDocumentRemoved = "brand.source.removed";

    /// <summary>
    /// Brought back out of the bin by an Owner. The <c>RemovedAt</c> and <c>RemovedBy</c> columns are cleared
    /// by the schema's own consistency constraint, so this pair of entries is the only lasting record that
    /// the document was ever removed.
    /// </summary>
    public const string SourceDocumentRestored = "brand.source.restored";

    /// <summary>
    /// One version was read as text by the Worker.
    /// </summary>
    /// <remarks>
    /// The only entry in this module with no actor, and correctly so: a background worker running as the
    /// workspace's service identity is not a person, and naming the creator who uploaded the file would
    /// attribute a machine's action to them. The entry says which version, which parser and how it ended —
    /// never a word of the document, which is what makes it safe to log at all.
    /// </remarks>
    public const string SourceDocumentExtracted = "brand.source.extracted";

    /// <summary>
    /// A creator replaced one version's extracted text with their own.
    /// </summary>
    /// <remarks>
    /// Names the actor, unlike <see cref="SourceDocumentExtracted"/>: this one is a person's edit to creator
    /// source material. The summary says which version and which ordinal and nothing else — the creator's stated
    /// reason is on the extraction row, and an audit summary is not where free text about a private document
    /// belongs.
    /// </remarks>
    public const string SourceDocumentTextCorrected = "brand.source.extraction.corrected";

    /// <summary>
    /// A creator asked for a version to be read again after the first attempt failed or stopped. Names the actor;
    /// the summary says which version and nothing else.
    /// </summary>
    public const string SourceExtractionRetried = "brand.source.extraction.retried";

    /// <summary>A chunk set built from a document's extracted text became the one retrieval reads.</summary>
    public const string SourceDocumentEmbedded = "brand.source.embedded";

    public const string StyleGuideResourceType = "BrandStyleGuide";

    /// <summary>A brand style guide and its first version were created.</summary>
    public const string StyleGuideCreated = "brand.guide.created";

    /// <summary>
    /// One guide version was approved: marked finished, which is what makes it activatable.
    /// </summary>
    /// <remarks>
    /// Audited separately from <see cref="StyleGuideActivated"/> because they are two decisions by possibly
    /// two people — an Editor settles the wording, an Owner makes it the workspace default — and the approval
    /// is the one that cannot be undone: <c>BrandStyleGuideApproval</c> is never withdrawn, so every
    /// generation that cites this version is traceable to this row. The after reference is the version
    /// approved, as <c>{guideId:N}:{versionNumber}</c>, matching activation's. There is no before reference:
    /// nothing was moved off. Ids and numbers only; the approver's stated reason stays on the approval row,
    /// because an audit summary is not where free text about a private guide belongs.
    /// </remarks>
    public const string StyleGuideVersionApproved = "brand.guide.version.approved";

    /// <summary>
    /// One approved guide version became the workspace's default.
    /// </summary>
    /// <remarks>
    /// The workspace's brand-voice decision, so it is audited for the reason auth.md gives for automation
    /// approval: it changes what every later generation is grounded on, and the record of who changed it has
    /// to outlive the row, which only holds the latest activation. The before and after references are the
    /// version moved off and the version moved to, each as <c>{guideId:N}:{versionNumber}</c> — the guide
    /// travels with the number because the default may move between guides, and a number alone would be
    /// ambiguous. Ids and numbers only: the activator's stated reason stays on the activation row, because an
    /// audit summary is not where free text about a private guide belongs.
    /// </remarks>
    public const string StyleGuideActivated = "brand.guide.activated";

    /// <summary>
    /// A new draft version was written from guidance a creator accepted out of an AI proposal (11A.18).
    /// </summary>
    /// <remarks>
    /// Its own action rather than a shared "version created", because the provenance is the point: this is the
    /// one way generated text enters a guide, and an audit trail that could not distinguish it from a version
    /// the creator typed would be unable to answer the only question worth asking about the row later. Names the
    /// actor — a creator accepted it, so it is a person's decision and not a machine's. The summary carries the
    /// proposal id and counts; no section body, no rule text, and nothing the model wrote, for the reason
    /// <see cref="StyleGuideCreated"/> gives.
    /// </remarks>
    public const string StyleGuideVersionCreatedFromProposal = "brand.guide.version.created_from_proposal";

    /// <summary>A creator edited a guide, which wrote a new draft version from their own words (11A.15).</summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="StyleGuideVersionCreatedFromProposal"/>, which is the same shape of
    /// write with a different provenance: this row says the words are the creator's. The summary carries the two
    /// version numbers and counts of what moved — no section body, no rule text and no change reason, for the
    /// reason <see cref="StyleGuideCreated"/> gives. A save that changed nothing writes no version and no row:
    /// there is nothing to audit about a guide that still says what it said.
    /// </remarks>
    public const string StyleGuideVersionEdited = "brand.guide.version.edited";

    public const string SetupSessionResourceType = "BrandSetupSession";

    /// <summary>A creator started a "Create my voice" session. The summary never carries the draft.</summary>
    public const string SetupSessionStarted = "brand.setup_session.started";

    public const string SetupSessionCompleted = "brand.setup_session.completed";

    /// <summary>The creator deleted their own session to start over. Touches no guide, source or profile.</summary>
    public const string SetupSessionReset = "brand.setup_session.reset";
}
