namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Where a requested test drive has got to, and the comparison once there is one (11A.24).
/// </summary>
/// <remarks>
/// <para>
/// A capability-specific reply rather than the shared <see cref="AiProposalStatusServiceModel"/>, because the
/// shared one publishes six change rows a client would have to pair up itself — and pairing them is the whole
/// reading of this result. The operation's own fields are carried through unchanged beside it, so polling a
/// test drive reads like polling anything else.
/// </para>
/// <para>
/// <strong>Nothing here can be accepted.</strong> There is no proposal id, no change id and no disposition,
/// because there is no route that would take one. A test drive is read.
/// </para>
/// </remarks>
/// <param name="RequestId">The id returned when the request was accepted, and the one to poll.</param>
/// <param name="Comparison">Null until the work has produced one, and when it failed.</param>
public sealed record BrandStyleTestDriveServiceModel(
    Guid RequestId,
    AiOperationStatus Status,
    AiFailureCategory? FailureCategory,
    DateTimeOffset RequestedAt,
    DateTimeOffset StatusChangedAt,
    BrandStyleTestDriveComparisonServiceModel? Comparison);

/// <summary>
/// What a creator reads: three samples written both ways, and what the guide contributed to the second of each.
/// </summary>
/// <param name="Subject">
/// What the samples are about — the creator's own words, or the platform's default when they named none. The
/// same subject reached both calls, which is what makes the two columns comparable.
/// </param>
/// <param name="GuideVersionWasActive">
/// Whether the version tested is the one the workspace writes with. False is the ordinary case for a creator
/// trying a draft out before activating it.
/// </param>
/// <param name="AppliedRules">
/// What the guide asks for, in the creator's own words, selected by the same rules the generation used — so
/// the screen can name the guidance behind the right-hand column instead of claiming it "sounds more like you".
/// </param>
/// <param name="Citations">
/// The passages of the creator's own examples that were sent with the request, as recorded at generation time.
/// Historical and exact, unlike <paramref name="AppliedRules"/>; see <paramref name="GroundingChangedSince"/>.
/// </param>
/// <param name="Notices">
/// What the server has to say about the grounding as a whole — guidance that did not apply, examples it could
/// not read — plus any caution the model attached to the answer rather than to one sample.
/// </param>
/// <param name="GroundingChangedSince">
/// True when the guide, the brand profile or the examples have changed since these samples were written, so
/// <paramref name="AppliedRules"/> describes the guide as it reads now rather than as it read then. The samples
/// are unaffected: they are what they were. A screen says so rather than quietly showing current wording beside
/// older writing.
/// </param>
public sealed record BrandStyleTestDriveComparisonServiceModel(
    string Subject,
    Guid GuideId,
    int GuideVersionNumber,
    bool GuideVersionWasActive,
    string ModelName,
    string PromptTemplateVersion,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<BrandStyleSamplePairServiceModel> Samples,
    IReadOnlyList<BrandWritingGuideRuleServiceModel> AppliedRules,
    IReadOnlyList<BrandStyleCitationServiceModel> Citations,
    IReadOnlyList<AiProposalWarningServiceModel> Notices,
    bool GroundingChangedSince);

/// <summary>
/// One sample, written both ways.
/// </summary>
/// <param name="Sample">
/// <c>blogIntro</c>, <c>socialCaption</c> or <c>imagePrompt</c> — the same names the stored rows use.
/// </param>
/// <param name="WithoutGuide">
/// Written by a call that received no brand context whatsoever: no guide, no profile, no examples. It is a
/// sample in its own right, not a "before".
/// </param>
/// <param name="Notes">Cautions the model attached to this sample, each saying which column it is about.</param>
public sealed record BrandStyleSamplePairServiceModel(
    string Sample,
    string WithoutGuide,
    string WithGuide,
    IReadOnlyList<BrandStyleSampleNoteServiceModel> Notes);

/// <param name="WithGuide">Which column the note is about. Both columns can carry their own.</param>
public sealed record BrandStyleSampleNoteServiceModel(
    bool WithGuide, AiWarningKind Kind, string Message);

/// <summary>
/// One passage of the creator's own writing that was sent with the guided call.
/// </summary>
/// <remarks>
/// No passage text: these point at creator content readable through the brand routes, and this reply is not the
/// place to re-serve it — the same reason <see cref="AiProposalBrandSourceServiceModel"/> carries none.
/// </remarks>
/// <param name="Title">
/// The creator's own title for the example, or null when the document has since been removed. Null is an answer:
/// the citation still names what was used.
/// </param>
public sealed record BrandStyleCitationServiceModel(
    Guid DocumentId,
    string? Title,
    int DocumentVersionNumber,
    Guid PassageId,
    int Ordinal);
