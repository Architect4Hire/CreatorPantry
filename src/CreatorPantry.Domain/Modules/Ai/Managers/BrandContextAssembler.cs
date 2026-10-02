using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Assembles the bounded brand context one generation is grounded on (11A.19).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A service rather than a facade, because its callers are task handlers.</strong> They already inject
/// the brand module's facades directly — <c>BrandGuideProposalAiTaskHandler</c> takes three — and no controller
/// calls this. Putting it behind a facade would add a layer whose only caller is a worker.
/// </para>
/// <para>
/// <strong>Everything it reads, it reads through the brand module's facades</strong>: the profile, the active
/// guide, the source library and the indexed passages. No repository, no <c>DbContext</c>, and so no way to
/// reach a row the resolved workspace cannot see. Cross-module traffic is facade to facade (backend.md).
/// </para>
/// <para>
/// <strong>It makes no provider call and builds no prompt.</strong> It hands over material; fencing it as
/// <c>PREFERENCES</c> and <c>REFERENCES</c> is <c>PromptEnvelopeBuilder</c>'s job, and every piece of text here
/// is creator data that must never be placed in an instruction segment.
/// </para>
/// <para>
/// <strong>The workspace is never a parameter.</strong> It comes from the resolved context through the facades
/// this calls, which is why nothing in <see cref="BrandContextRequest"/> names one.
/// </para>
/// </remarks>
public interface IBrandContextAssembler
{
    /// <summary>
    /// The brand context for one request, or a refusal when the request cannot be honoured at all.
    /// </summary>
    /// <returns>
    /// The package — which may legitimately carry no guide, no profile and no excerpts, each stated as an
    /// omission — or <c>ai.brandContextChannel.invalid_request</c>, <c>ai.brandContextGuide.not_found</c>, or
    /// <c>ai.brandContextSource.invalid_request</c>.
    /// </returns>
    Task<OperationResult<BrandContextPackage>> AssembleAsync(
        BrandContextRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBrandContextAssembler"/>
internal sealed class BrandContextAssembler(
    IBrandProfileFacade profiles,
    IBrandStyleGuideFacade guides,
    IBrandSourceDocumentFacade documents,
    IBrandSourcePassageFacade passages,
    IContentChannelCatalog channels,
    IClock clock) : IBrandContextAssembler
{
    public async Task<OperationResult<BrandContextPackage>> AssembleAsync(
        BrandContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelKey = Normalize(request.ChannelKey);

        // Checked against the catalogue before anything is read, so a key the product does not know is refused
        // rather than used to go looking for guidance that could not exist.
        if (channelKey is not null && channels.Find(channelKey) is null)
        {
            return Failure(
                BrandContextErrors.ChannelInvalid,
                "That is not a channel this product knows how to write for.");
        }

        var named = request.SourceDocumentIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];

        // Refused rather than trimmed: a caller that asked for twelve documents and silently got ten would
        // believe it had read all twelve.
        if (named.Count > AiPolicy.BrandContextMaxSourceDocuments)
        {
            return Failure(
                BrandContextErrors.TooManySourceDocuments,
                $"At most {AiPolicy.BrandContextMaxSourceDocuments} source documents may ground one generation.");
        }

        var conflicts = new SortedSet<BrandContextConflict>();
        var omissions = new SortedSet<BrandContextOmission>();

        // A task the table grounds in nothing gets nothing — not the guidance, not the rules, not the profile,
        // and not even a guide id. Structural rather than conventional: the capabilities that produce or judge
        // recipe facts are the ones brand voice must never reach (11A.20's restriction), and an early return is
        // the only version of that guarantee a later edit cannot quietly undo by populating one more field.
        if (!BrandContextSelection.AppliesTo(request.TaskType))
        {
            return OperationResult<BrandContextPackage>.Success(Empty(request.TaskType, channelKey));
        }

        var profile = await ReadProfileAsync(omissions, cancellationToken);
        var guide = await ReadGuideAsync(request.Guide, conflicts, omissions, cancellationToken);

        if (guide.Refusal is { } refusal)
        {
            return Failure(refusal.Code, refusal.Message);
        }

        var audience = ResolveAudience(request, profile, guide.Version, conflicts);

        if (channelKey is not null
            && profile is not null
            && !profile.ChannelDefaults.Contains(channelKey, StringComparer.Ordinal))
        {
            conflicts.Add(BrandContextConflict.ChannelNotABrandDefault);
        }

        var guidance = SelectGuidance(request.TaskType, channelKey, guide.Version, conflicts, omissions);
        // Guide rules are guide-wide Do/Don't lines written for the brand's voice, with no section to say otherwise,
        // so an image task does not receive them: its negative guidance is the NegativeVisualGuidance section.
        var rules = guide.Version is { } version && !BrandContextSelection.IsVisual(request.TaskType)
            ? version.Rules.Select(rule => new BrandContextRule(rule.Kind, rule.Text)).ToList()
            : [];

        var excerpts = await ReadExcerptsAsync(
            request.TaskType, channelKey, audience, named, conflicts, omissions, cancellationToken);

        var budgeted = BrandContextSelection.Spend(
            AiPolicy.BrandContextMaxEstimatedTokens, profile, audience, guidance, rules, excerpts);

        if (budgeted.DroppedGuidance > 0)
        {
            omissions.Add(BrandContextOmission.GuideSectionOverBudget);
        }

        if (budgeted.DroppedExcerpts > 0)
        {
            omissions.Add(BrandContextOmission.ExcerptOverBudget);
        }

        // After the budget, so the checksum names what the package actually carries rather than what was
        // selected before trimming. A client re-reading a generation's provenance has to be able to match it.
        var checksum = BrandContextSelection.Checksum(
            request.TaskType,
            channelKey,
            audience,
            profile,
            guide.Version?.Id,
            guide.Version?.VersionNumber,
            budgeted.Guidance,
            rules,
            budgeted.Excerpts);

        return OperationResult<BrandContextPackage>.Success(new BrandContextPackage(
            request.TaskType,
            channelKey,
            audience,
            audience is null ? null : ResolveAudienceOrigin(request, profile, guide.Version),
            profile,
            guide.GuideId,
            guide.Version?.Id,
            guide.Version?.VersionNumber,
            guide.IsActiveVersion,
            budgeted.Guidance,
            rules,
            budgeted.Excerpts,
            [.. conflicts],
            [.. omissions],
            budgeted.EstimatedTokens,
            checksum,
            clock.UtcNow));
    }

    /// <summary>
    /// The package a task that is not grounded in brand context gets: the request it answered, and nothing else.
    /// </summary>
    /// <remarks>
    /// Still carries a checksum, so a generation recording its provenance records something that identifies
    /// "assembled, and deliberately empty" rather than a null a reader cannot tell from a missing step.
    /// </remarks>
    private BrandContextPackage Empty(AiTaskType taskType, string? channelKey) => new(
        taskType,
        channelKey,
        Audience: null,
        AudienceOrigin: null,
        Profile: null,
        GuideId: null,
        GuideVersionId: null,
        GuideVersionNumber: null,
        GuideIsActiveVersion: false,
        Guidance: [],
        Rules: [],
        Excerpts: [],
        Conflicts: [],
        Omissions: [],
        EstimatedTokens: 0,
        BrandContextSelection.Checksum(taskType, channelKey, null, null, null, null, [], [], []),
        clock.UtcNow);

    /// <summary>The brand profile's facts, or null with an omission recorded when the workspace has none.</summary>
    private async Task<BrandContextProfile?> ReadProfileAsync(
        SortedSet<BrandContextOmission> omissions, CancellationToken cancellationToken)
    {
        var read = await profiles.GetAsync(cancellationToken);

        if (!read.Succeeded)
        {
            // A workspace that has not created one yet. An omission, not a refusal: a generation can proceed
            // without brand facts, and saying so is better than failing the request.
            omissions.Add(BrandContextOmission.NoBrandProfile);

            return null;
        }

        var profile = read.Value!;

        return new BrandContextProfile(
            profile.BrandName,
            profile.ShortDescription,
            profile.DefaultAudience,
            profile.Locale,
            [.. profile.ChannelDefaults.Select(channel => channel.ChannelKey)],
            profile.Revision);
    }

    /// <summary>What guide version applied, if any, and why.</summary>
    private sealed record GuideResolution(
        Guid? GuideId,
        BrandStyleGuideVersionDetailServiceModel? Version,
        bool IsActiveVersion,
        (string Code, string Message)? Refusal = null);

    /// <summary>
    /// Resolves the guide version to ground on: the one the request named, or the workspace's active one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No fallback, which is the restriction's own words.</strong> A selection that does not resolve
    /// refuses; it does not quietly become the active guide, because a generation grounded on a guide the caller
    /// did not ask for is worse than one that did not run. A workspace with no activation and no selection gets
    /// no guide and an omission — that is the no-guide case, and it is legitimate.
    /// </para>
    /// <para>
    /// An unapproved or non-active selection is reported as a conflict and still used. The caller named that
    /// exact version, and activation already guarantees the <em>active</em> one is approved — so the only way
    /// here is an explicit choice, which is the caller's to make and the package's to disclose.
    /// </para>
    /// </remarks>
    private async Task<GuideResolution> ReadGuideAsync(
        BrandGuideSelection? selection,
        SortedSet<BrandContextConflict> conflicts,
        SortedSet<BrandContextOmission> omissions,
        CancellationToken cancellationToken)
    {
        var active = await guides.GetActiveAsync(cancellationToken);

        if (selection is null)
        {
            if (!active.Succeeded || active.Value is null)
            {
                omissions.Add(BrandContextOmission.NoActiveGuide);

                return new GuideResolution(null, null, IsActiveVersion: false);
            }

            return new GuideResolution(active.Value.GuideId, active.Value.Version, IsActiveVersion: true);
        }

        var guide = await guides.GetAsync(selection.GuideId, cancellationToken);

        if (!guide.Succeeded)
        {
            // Answered identically to a guide that was never created, and to another workspace's (tenancy.md).
            return new GuideResolution(
                null,
                null,
                IsActiveVersion: false,
                (BrandContextErrors.GuideSelectionNotFound, "That brand style guide version does not exist."));
        }

        // The working version is the only one this read publishes in full, so a selection naming any other
        // number cannot be honoured — and refusing says so rather than grounding on a version nobody named.
        var version = guide.Value!.WorkingVersion.VersionNumber == selection.VersionNumber
            ? guide.Value.WorkingVersion
            : guide.Value.ActiveVersion?.VersionNumber == selection.VersionNumber
                ? guide.Value.ActiveVersion
                : null;

        if (version is null)
        {
            return new GuideResolution(
                null,
                null,
                IsActiveVersion: false,
                (BrandContextErrors.GuideSelectionNotFound, "That brand style guide version does not exist."));
        }

        var isActive = active.Succeeded && active.Value?.Version.Id == version.Id;

        if (!isActive)
        {
            conflicts.Add(BrandContextConflict.GuideVersionNotActive);
        }

        if (version.Approval is null)
        {
            conflicts.Add(BrandContextConflict.GuideVersionUnapproved);
        }

        return new GuideResolution(selection.GuideId, version, isActive);
    }

    /// <summary>
    /// Which audience wins, by the per-field precedence rule.
    /// </summary>
    /// <remarks>
    /// Per field, not per source: a guide whose <c>Audience</c> section says nothing does not shadow the
    /// profile's default, and a channel variant silent on audience does not either. The request wins over both,
    /// and the disagreement is recorded rather than hidden.
    /// </remarks>
    private static string? ResolveAudience(
        BrandContextRequest request,
        BrandContextProfile? profile,
        BrandStyleGuideVersionDetailServiceModel? version,
        SortedSet<BrandContextConflict> conflicts)
    {
        var requested = Normalize(request.Audience);
        var profileDefault = ProfileAudience(profile);

        if (requested is not null)
        {
            if (profileDefault is not null && !string.Equals(requested, profileDefault, StringComparison.Ordinal))
            {
                conflicts.Add(BrandContextConflict.AudienceOverridesProfile);
            }

            return requested;
        }

        return GuideAudience(version) ?? profileDefault;
    }

    /// <summary>Which level the resolved audience came from. Computed the same way the value was.</summary>
    private static BrandContextOrigin ResolveAudienceOrigin(
        BrandContextRequest request,
        BrandContextProfile? profile,
        BrandStyleGuideVersionDetailServiceModel? version) =>
        Normalize(request.Audience) is not null
            ? BrandContextOrigin.Request
            : GuideAudience(version) is not null
                ? BrandContextOrigin.GuideSection
                : BrandContextOrigin.BrandProfile;

    /// <summary>
    /// The guide's own statement of who it writes for, from its <c>Audience</c> section.
    /// </summary>
    /// <remarks>
    /// Read off a section rather than a dedicated field, because that is where the creator put it: a guide's
    /// audience is prose they wrote, not an enum. It is treated as a value here and still travels as guidance,
    /// so nothing is lost by resolving it.
    /// </remarks>
    private static string? GuideAudience(BrandStyleGuideVersionDetailServiceModel? version) =>
        Normalize(version?.Sections
            .FirstOrDefault(section => section.SectionKey is BrandStyleGuideSectionKey.Audience)
            ?.Body);

    private static string? ProfileAudience(BrandContextProfile? profile) => Normalize(profile?.DefaultAudience);

    /// <summary>
    /// Selects the guidance that applies, by task and channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The channel variant for the requested channel is included; a variant for any other channel is not, and
    /// where the guide holds variants but none for this channel that is reported — a creator who wrote
    /// Instagram rules and is generating for Pinterest would otherwise watch their guidance simply not apply.
    /// </para>
    /// <para>
    /// A section key the task wanted and the guide does not hold is an omission. One omission for all of them
    /// rather than one each: the package says the guide is incomplete for this task, and which keys those are is
    /// derivable by the caller from the task and what arrived.
    /// </para>
    /// </remarks>
    internal static List<BrandContextGuidance> SelectGuidance(
        AiTaskType taskType,
        string? channelKey,
        BrandStyleGuideVersionDetailServiceModel? version,
        SortedSet<BrandContextConflict> conflicts,
        SortedSet<BrandContextOmission> omissions)
    {
        var wanted = BrandContextSelection.SectionKeysFor(taskType);

        if (version is null || wanted.Count == 0)
        {
            return [];
        }

        var guidance = new List<BrandContextGuidance>();

        foreach (var key in wanted)
        {
            var section = version.Sections.FirstOrDefault(candidate => candidate.SectionKey == key);

            if (section is null)
            {
                omissions.Add(BrandContextOmission.GuideSectionMissing);
                continue;
            }

            guidance.Add(new BrandContextGuidance(
                key, null, section.Body, BrandContextOrigin.GuideSection));
        }

        var variants = version.Sections
            .Where(section => section.SectionKey is BrandStyleGuideSectionKey.ChannelVariant)
            .ToList();

        var variant = channelKey is null
            ? null
            : variants.FirstOrDefault(section => string.Equals(section.ChannelKey, channelKey, StringComparison.Ordinal));

        if (variant is not null)
        {
            guidance.Add(new BrandContextGuidance(
                BrandStyleGuideSectionKey.ChannelVariant,
                variant.ChannelKey,
                variant.Body,
                BrandContextOrigin.GuideChannelVariant));
        }
        else if (channelKey is not null && variants.Count > 0)
        {
            conflicts.Add(BrandContextConflict.ChannelVariantForAnotherChannelOnly);
        }

        return guidance;
    }

    /// <summary>
    /// Reads the excerpts: from the documents the caller named, or from a bounded relevance match when it named
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Never every document.</strong> A named list is capped and authorized one document at a time; an
    /// unnamed one is matched on the facts the library already stores — purpose, channel, audience — and capped
    /// lower still, because a guess the server makes on the creator's behalf should spend less of the budget
    /// than an instruction.
    /// </para>
    /// <para>
    /// Versions are pinned from each document's current version, server-side, so the package records the exact
    /// text it was grounded in even after the document is replaced. A named document that does not resolve is an
    /// omission rather than a refusal: the remaining evidence is still worth having, and the package says what
    /// it did not get.
    /// </para>
    /// </remarks>
    private async Task<List<BrandContextExcerpt>> ReadExcerptsAsync(
        AiTaskType taskType,
        string? channelKey,
        string? audience,
        IReadOnlyList<Guid> named,
        SortedSet<BrandContextConflict> conflicts,
        SortedSet<BrandContextOmission> omissions,
        CancellationToken cancellationToken)
    {
        if (!BrandContextSelection.AppliesTo(taskType))
        {
            return [];
        }

        var selectors = named.Count > 0
            ? await PinNamedAsync(named, channelKey, audience, conflicts, omissions, cancellationToken)
            : await PinRelevantAsync(taskType, channelKey, audience, cancellationToken);

        if (selectors.Count == 0)
        {
            omissions.Add(BrandContextOmission.NoSourceExcerpts);

            return [];
        }

        var supplied = await passages.ListPassagesAsync(selectors, cancellationToken);

        // A visual reference the creator named is used through its indexed text only. One that is readable but has
        // none — an image the extractor cannot read — is reported as such, and instead of the generic
        // "unavailable" notice, so the creator hears one thing. Its bytes are never sent in place of the text.
        var unsupplied = BrandContextSelection.IsVisual(taskType) && named.Count > 0
            ? selectors.Where(selector => supplied.Passages.All(passage => passage.DocumentId != selector.DocumentId)).ToList()
            : [];

        if (unsupplied.Count > 0)
        {
            omissions.Add(BrandContextOmission.NoVisualReferenceText);
        }

        if (supplied.Unavailable.Any(gone => unsupplied.All(item => item.DocumentId != gone.DocumentId)))
        {
            omissions.Add(BrandContextOmission.SourceDocumentUnavailable);
        }

        if (supplied.Passages.Count == 0)
        {
            omissions.Add(BrandContextOmission.NoSourceExcerpts);
        }

        return
        [
            .. supplied.Passages.Select(passage => new BrandContextExcerpt(
                passage.PassageId, passage.DocumentId, passage.VersionNumber, passage.Ordinal, passage.Text)),
        ];
    }

    /// <summary>Pins the current version of each named document, reporting the ones that do not resolve.</summary>
    private async Task<List<BrandSourcePassageSelector>> PinNamedAsync(
        IReadOnlyList<Guid> named,
        string? channelKey,
        string? audience,
        SortedSet<BrandContextConflict> conflicts,
        SortedSet<BrandContextOmission> omissions,
        CancellationToken cancellationToken)
    {
        var selectors = new List<BrandSourcePassageSelector>(named.Count);

        foreach (var documentId in named)
        {
            var document = await documents.GetAsync(documentId, cancellationToken);

            if (!document.Succeeded)
            {
                // Never issued, another workspace's, or removed — one answer for all three, and the package
                // reports that something was asked for and not supplied.
                omissions.Add(BrandContextOmission.SourceDocumentUnavailable);
                continue;
            }

            var detail = document.Value!;

            // Reported, not excluded. The creator named this document for this job; a mismatch is worth telling
            // them about, and overriding their choice on a tag would be the assembler second-guessing them.
            if (channelKey is not null
                && detail.ChannelKey is not null
                && !string.Equals(detail.ChannelKey, channelKey, StringComparison.Ordinal))
            {
                conflicts.Add(BrandContextConflict.SourceDocumentChannelMismatch);
            }

            if (audience is not null
                && detail.Audience is not null
                && !string.Equals(detail.Audience, audience, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(BrandContextConflict.SourceDocumentAudienceMismatch);
            }

            selectors.Add(new BrandSourcePassageSelector(documentId, detail.CurrentVersion.VersionNumber));
        }

        return selectors;
    }

    /// <summary>
    /// Asks the brand module which of its documents are worth grounding this job in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The ranking lives in the brand module, not here.</strong> The facts a document is ranked on — its
    /// purpose, channel and audience — are that module's, and the library query that reads them is its own; an
    /// assembler building one would have had to name a view model across the boundary, which is a defect
    /// <c>ModuleBoundaryTests</c> refuses. What this module decides is the <em>budget</em>: which purposes are
    /// usable, and how many documents a guess may spend.
    /// </para>
    /// <para>
    /// Bounded by <see cref="AiPolicy.BrandContextMaxSelectedSourceDocuments"/>, which is lower than the cap on
    /// a list the caller named: a guess the server makes on the creator's behalf should spend less of the budget
    /// than an instruction.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<BrandSourcePassageSelector>> PinRelevantAsync(
        AiTaskType taskType, string? channelKey, string? audience, CancellationToken cancellationToken) =>
        await documents.ListGroundingCandidatesAsync(
            BrandContextSelection.PurposesFor(taskType),
            channelKey,
            audience,
            AiPolicy.BrandContextMaxSelectedSourceDocuments,
            cancellationToken);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OperationResult<BrandContextPackage> Failure(string code, string message) =>
        OperationResult<BrandContextPackage>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
