namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What an accepted brand-guide proposal asks this module to write (11A.18): guidance the creator reviewed, in
/// this module's own vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A purpose-built input type the brand module owns</strong>, the analogue of the recipe module's
/// <c>ProposedRecipeDraft</c>. Naming it from the AI module is the documented way across the boundary rather
/// than an exception to it: the AI module may name this type and the ServiceModels the facade returns, and
/// nothing else in here (backend.md).
/// </para>
/// <para>
/// <strong>There is no dimension, evidence basis, passage id or change id in it.</strong> Those belong to a
/// proposal, and a proposal is not a guide. What crosses is the text, where it goes, and which exact source
/// versions it rests on — translated by the caller, because the two vocabularies are deliberately not the same
/// (see <c>AiBrandGuideDimension</c>'s own remarks).
/// </para>
/// <para>
/// <strong>Nothing here can activate anything.</strong> There is no approval, no default and no status: the
/// version this writes is a draft like every other, and becoming the workspace's default still needs an
/// approval and an Owner going through <c>ActivateVersionAsync</c>.
/// </para>
/// </remarks>
/// <param name="AiProposalId">
/// The proposal the guidance was accepted from, for the audit entry. Recorded as an id and never as content.
/// </param>
/// <param name="ExpectedWorkingVersionNumber">
/// The working version the caller composed this against. Checked against the guide as it actually stands, so
/// accepted guidance cannot be rebased onto a version the creator never compared it with.
/// </param>
/// <param name="Sections">
/// The accepted sections. One per <c>(SectionKey, ChannelKey)</c>; a key the working version already holds is
/// replaced, and one it does not is added.
/// </param>
/// <param name="Rules">The accepted do/don't rules. Appended after the working version's, deduplicated.</param>
/// <param name="CitedSources">
/// The exact source document versions the accepted guidance cites, as the proposal pinned them. Unioned with
/// what the working version already cites; never re-pointed at a document's newer version.
/// </param>
/// <param name="ChangeReason">The creator's own note on the change, or null.</param>
public sealed record BrandStyleGuideProposalApplication(
    Guid AiProposalId,
    int ExpectedWorkingVersionNumber,
    IReadOnlyList<BrandStyleGuideSectionServiceModel> Sections,
    IReadOnlyList<BrandStyleGuideRuleServiceModel> Rules,
    IReadOnlyList<BrandStyleGuideSourceServiceModel> CitedSources,
    string? ChangeReason);

/// <summary>
/// The version an accepted proposal produced, or the fact that it produced none.
/// </summary>
/// <param name="GuideId">The guide written to.</param>
/// <param name="VersionId">
/// The new version, or <c>null</c> when nothing was written because the accepted guidance already matched the
/// working version word for word.
/// </param>
/// <param name="VersionNumber">The new version's number, or <c>null</c> for that same no-op.</param>
/// <param name="ParentVersionNumber">The working version it was built from.</param>
/// <param name="SectionsAdded">Accepted sections the working version had no row for.</param>
/// <param name="SectionsReplaced">Accepted sections that replaced one it did.</param>
/// <param name="RulesAdded">Accepted rules that were not already there.</param>
/// <param name="RulesAlreadyPresent">
/// Accepted rules the working version already held, matched the way the create validator matches them. Reported
/// rather than dropped silently: a creator who ticked three rules and got one is entitled to know why.
/// </param>
/// <param name="SourceCount">How many source versions the new version cites in total.</param>
/// <param name="StaleSourceCount">
/// How many of those the owning document has since replaced. Not a refusal — the citation is pinned to the
/// version the proposal read, which is the honest record of what the guidance rests on — but it is reported,
/// because a version with a stale citation cannot be activated until it is rewritten from current sources.
/// </param>
/// <remarks>
/// <strong>A null <paramref name="VersionId"/> is the no-op rule a creator's own edit follows</strong>, not a
/// failure: accepting guidance identical to what the guide already says changes nothing, and writing a version
/// to record that nothing changed would make the history harder to read rather than more complete.
/// </remarks>
public sealed record BrandStyleGuideVersionCreatedServiceModel(
    Guid GuideId,
    Guid? VersionId,
    int? VersionNumber,
    int ParentVersionNumber,
    int SectionsAdded,
    int SectionsReplaced,
    int RulesAdded,
    int RulesAlreadyPresent,
    int SourceCount,
    int StaleSourceCount);
