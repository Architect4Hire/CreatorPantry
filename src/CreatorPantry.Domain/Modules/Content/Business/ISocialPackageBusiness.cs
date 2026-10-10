using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>
/// The post package's rules (AF.6.1): what a revision may be written from, which moves a channel may make and
/// who may make them, and when accepted words stop being current.
/// </summary>
/// <remarks>
/// Every move is about one channel. Nothing here reads or writes a neighbouring slot, which is what lets each
/// channel be accepted, rejected or regenerated on its own.
/// </remarks>
public interface ISocialPackageBusiness
{
    /// <summary>The context's posts; a null value when the context exists and nothing has been written for it.</summary>
    Task<OperationResult<SocialPackageServiceModel?>> GetAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>Stores a generated body as the channel's newest revision, awaiting a decision.</summary>
    Task<OperationResult<SocialPackageServiceModel>> RecordGeneratedAsync(
        SocialGeneratedRevisionInput input, CancellationToken cancellationToken);

    /// <summary>Stores the creator's own wording as the channel's newest revision, awaiting a decision.</summary>
    Task<OperationResult<SocialPackageServiceModel>> RecordEditAsync(
        SocialEditedRevisionInput input, CancellationToken cancellationToken);

    /// <summary>Accepts the channel's newest revision, which <paramref name="revisionId"/> must name.</summary>
    Task<OperationResult<SocialPackageServiceModel>> AcceptAsync(
        string actorUserId, Guid contextId, string channelKey, Guid revisionId, CancellationToken cancellationToken);

    /// <summary>Declines the channel's newest revision. Anything accepted earlier is kept.</summary>
    Task<OperationResult<SocialPackageServiceModel>> RejectAsync(
        string actorUserId, Guid contextId, string channelKey, Guid revisionId, CancellationToken cancellationToken);

    /// <summary>Confirms stale accepted words still stand, as a new revision pinned to the recipe as it is now.</summary>
    Task<OperationResult<SocialPackageServiceModel>> ReaffirmAsync(
        string actorUserId, Guid contextId, string channelKey, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISocialPackageBusiness"/>
internal sealed class SocialPackageBusiness(
    ISocialPackageDataLayer dataLayer,
    IContentChannelCatalog channels,
    IWorkspaceContext workspace,
    IClock clock) : ISocialPackageBusiness
{
    private const string CannotSave = "That post could not be saved as described.";

    public async Task<OperationResult<SocialPackageServiceModel?>> GetAsync(Guid contextId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindContextFactsAsync(contextId, cancellationToken) is null)
        {
            return OperationResult<SocialPackageServiceModel?>.Failure(NotFound());
        }

        var snapshot = await dataLayer.FindAsync(contextId, cancellationToken);

        return OperationResult<SocialPackageServiceModel?>.Success(snapshot is null ? null : ToServiceModel(snapshot));
    }

    public async Task<OperationResult<SocialPackageServiceModel>> RecordGeneratedAsync(
        SocialGeneratedRevisionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (Invalid(SocialPackageInputChecks.Generated(input)) is { } invalid)
        {
            return invalid;
        }

        var (slot, refusal) = await OpenSlotAsync(input.CreativeContextId, input.ChannelKey, cancellationToken);

        if (slot is null)
        {
            return Failure(refusal!);
        }

        // The pin is what the caller grounded on, so it is taken as given — but only for a recipe this
        // context actually names. A post about a recipe the work is not about would go stale on a change the
        // creator has no reason to connect with it.
        if (input.RecipeId is { } recipeId && !slot.Facts.RecipeIds.Contains(recipeId))
        {
            return Failure(SourceRefused());
        }

        var revision = NewRevision(slot, input.Body, input.Limit, ContentRevisionSource.AiGenerated);
        revision.AiProposalId = input.AiProposalId;
        revision.PromptTemplateId = input.PromptTemplateId;
        revision.PromptTemplateVersion = input.PromptTemplateVersion;
        revision.PromptTemplateBodyChecksum = input.PromptTemplateBodyChecksum;
        revision.RecipeId = input.RecipeId;
        revision.RecipeVersionId = input.RecipeVersionId;
        revision.BrandProfileRevisionId = input.BrandProfileRevisionId;
        revision.BrandStyleGuideVersionId = input.BrandStyleGuideVersionId;
        revision.CreativeContextVersion = input.CreativeContextVersion;
        revision.ContextPackageChecksum = input.ContextPackageChecksum;

        // A refused row here is a pin the keys would not take — the checks above cannot see another
        // workspace's version, revision or proposal, and neither can the caller be told which it was.
        return await SaveDraftAsync(slot, revision, onRefused: SourceRefused(), cancellationToken);
    }

    public async Task<OperationResult<SocialPackageServiceModel>> RecordEditAsync(
        SocialEditedRevisionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (Invalid(SocialPackageInputChecks.Edited(input)) is { } invalid)
        {
            return invalid;
        }

        var (slot, refusal) = await OpenSlotAsync(input.CreativeContextId, input.ChannelKey, cancellationToken);

        if (slot is null)
        {
            return Failure(refusal!);
        }

        // An edit is of the words its author was looking at. One composed against an older revision is
        // refused rather than stacked on a newer one they never saw.
        if (slot.Latest?.Id != input.ExpectedLatestRevisionId)
        {
            return Failure(Stale());
        }

        var revision = NewRevision(slot, input.Body, input.Limit, ContentRevisionSource.CreatorEdit);

        // Carried forward, because an edit does not change which template or voice the words began from.
        revision.PromptTemplateId = slot.Latest?.PromptTemplateId;
        revision.PromptTemplateVersion = slot.Latest?.PromptTemplateVersion;
        revision.PromptTemplateBodyChecksum = slot.Latest?.PromptTemplateBodyChecksum;
        revision.BrandProfileRevisionId = slot.Latest?.BrandProfileRevisionId;
        revision.BrandStyleGuideVersionId = slot.Latest?.BrandStyleGuideVersionId;
        revision.CreativeContextVersion = slot.Latest?.CreativeContextVersion;
        revision.ContextPackageChecksum = slot.Latest?.ContextPackageChecksum;

        // The recipe pin is not carried: the creator wrote these words now, against the recipe as it stands
        // now, so that is the version they are pinned to. The same reading a reaffirmation takes.
        var recipeId = slot.Latest is not null
            ? slot.Latest.RecipeId
            : slot.Facts.RecipeIds.Count > 0 ? (Guid?)slot.Facts.RecipeIds[0] : null;

        if (recipeId is { } id && await dataLayer.FindLatestRecipeVersionAsync(id, cancellationToken) is { } newest)
        {
            revision.RecipeId = id;
            revision.RecipeVersionId = newest.Id;
        }

        return await SaveDraftAsync(slot, revision, onRefused: Stale(), cancellationToken);
    }

    public async Task<OperationResult<SocialPackageServiceModel>> AcceptAsync(
        string actorUserId, Guid contextId, string channelKey, Guid revisionId, CancellationToken cancellationToken)
    {
        var rule = ContentProposalTransitions.Find(ContentProposalStatus.Proposed, ContentProposalStatus.Accepted);

        if (!Allowed(rule))
        {
            return Failure(Forbidden());
        }

        var package = await dataLayer.FindForUpdateAsync(contextId, cancellationToken);
        var channel = FindChannel(package, channelKey);

        if (package is null || channel is null)
        {
            return Failure(NotFound());
        }

        // Accepting what is already accepted is the same decision made twice: nothing is written, and no
        // second audit entry claims a second decision.
        if (channel.Status == ContentProposalStatus.Accepted && channel.AcceptedRevisionId == revisionId)
        {
            return await ReadAsync(contextId, cancellationToken);
        }

        if (channel.Status != ContentProposalStatus.Proposed)
        {
            return Failure(DecisionConflict(channel.Status));
        }

        var latest = await dataLayer.FindLatestRevisionAsync(channel.Id, cancellationToken);

        // Only the newest revision can be decided on, and the caller must name it: a decision composed while
        // looking at an older body must not land on words its author has not read.
        if (latest is null || latest.Id != revisionId)
        {
            return Failure(Stale());
        }

        // The rule ContentProposalTransitions leaves to whichever seam writes the acceptance: words pinned to
        // a recipe version that is no longer the latest cannot be accepted as current.
        if (await IsPinStaleAsync(latest, cancellationToken))
        {
            return Failure(new OperationError(
                ContentErrorCodes.SocialSourceStale,
                "The recipe has changed since this post was written. Edit or regenerate it before accepting.",
                new Dictionary<string, string[]>()));
        }

        var now = clock.UtcNow;
        var before = channel.Status;

        channel.Status = ContentProposalStatus.Accepted;
        channel.AcceptedRevisionId = latest.Id;
        channel.UpdatedAt = now;
        package.UpdatedAt = now;

        return await SaveDecisionAsync(
            package,
            revision: null,
            Audit(
                actorUserId,
                ContentAuditActions.SocialChannelAccepted,
                channel,
                $"Accepted revision {latest.RevisionNumber} of the {channel.ChannelKey} post.",
                before),
            cancellationToken);
    }

    public async Task<OperationResult<SocialPackageServiceModel>> RejectAsync(
        string actorUserId, Guid contextId, string channelKey, Guid revisionId, CancellationToken cancellationToken)
    {
        var rule = ContentProposalTransitions.Find(ContentProposalStatus.Proposed, ContentProposalStatus.Rejected);

        if (!Allowed(rule))
        {
            return Failure(Forbidden());
        }

        var package = await dataLayer.FindForUpdateAsync(contextId, cancellationToken);
        var channel = FindChannel(package, channelKey);

        if (package is null || channel is null)
        {
            return Failure(NotFound());
        }

        var latest = await dataLayer.FindLatestRevisionAsync(channel.Id, cancellationToken);

        if (latest is null || latest.Id != revisionId)
        {
            return Failure(Stale());
        }

        if (channel.Status == ContentProposalStatus.Rejected)
        {
            return await ReadAsync(contextId, cancellationToken);
        }

        if (channel.Status != ContentProposalStatus.Proposed)
        {
            return Failure(DecisionConflict(channel.Status));
        }

        var now = clock.UtcNow;
        var before = channel.Status;

        // The accepted pointer is left exactly where it was: declining the newest words takes nothing away
        // from the ones accepted before them.
        channel.Status = ContentProposalStatus.Rejected;
        channel.UpdatedAt = now;
        package.UpdatedAt = now;

        return await SaveDecisionAsync(
            package,
            revision: null,
            Audit(
                actorUserId,
                ContentAuditActions.SocialChannelRejected,
                channel,
                $"Rejected revision {latest.RevisionNumber} of the {channel.ChannelKey} post.",
                before),
            cancellationToken);
    }

    public async Task<OperationResult<SocialPackageServiceModel>> ReaffirmAsync(
        string actorUserId, Guid contextId, string channelKey, CancellationToken cancellationToken)
    {
        var rule = ContentProposalTransitions.Find(ContentProposalStatus.NeedsReview, ContentProposalStatus.Accepted);

        if (!Allowed(rule))
        {
            return Failure(Forbidden());
        }

        var package = await dataLayer.FindForUpdateAsync(contextId, cancellationToken);
        var channel = FindChannel(package, channelKey);

        if (package is null || channel is null)
        {
            return Failure(NotFound());
        }

        if (channel.Status != ContentProposalStatus.NeedsReview)
        {
            return Failure(DecisionConflict(channel.Status));
        }

        var accepted = await dataLayer.FindRevisionAsync(channel.Id, channel.AcceptedRevisionId!.Value, cancellationToken);
        var latest = await dataLayer.FindLatestRevisionAsync(channel.Id, cancellationToken);

        if (accepted is null || latest is null)
        {
            return Failure(Stale());
        }

        var now = clock.UtcNow;

        // The same words under a new pin. The stale revision keeps the pin it was accepted with, so history
        // still says what was accepted against what; this row records that the creator looked again.
        var reaffirmed = new SocialRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            SocialPackageChannelId = channel.Id,
            RevisionNumber = latest.RevisionNumber + 1,
            ParentRevisionId = accepted.Id,
            Source = ContentRevisionSource.Reaffirmed,
            Body = accepted.Body,
            CharacterCount = accepted.CharacterCount,
            CharacterLimit = accepted.CharacterLimit,
            LimitStatus = accepted.LimitStatus,
            ChannelProfileVersion = accepted.ChannelProfileVersion,
            BrandProfileRevisionId = accepted.BrandProfileRevisionId,
            BrandStyleGuideVersionId = accepted.BrandStyleGuideVersionId,
            PromptTemplateId = accepted.PromptTemplateId,
            PromptTemplateVersion = accepted.PromptTemplateVersion,
            PromptTemplateBodyChecksum = accepted.PromptTemplateBodyChecksum,
            CreativeContextVersion = accepted.CreativeContextVersion,
            ContextPackageChecksum = accepted.ContextPackageChecksum,
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
        };

        if (accepted.RecipeId is { } recipeId
            && await dataLayer.FindLatestRecipeVersionAsync(recipeId, cancellationToken) is { } newest)
        {
            reaffirmed.RecipeId = recipeId;
            reaffirmed.RecipeVersionId = newest.Id;
        }

        channel.Status = ContentProposalStatus.Accepted;
        channel.AcceptedRevisionId = reaffirmed.Id;
        channel.StaleSince = null;
        channel.StaleReasons = ContentStaleReasons.None;
        channel.UpdatedAt = now;
        package.UpdatedAt = now;

        return await SaveDecisionAsync(
            package,
            reaffirmed,
            Audit(
                actorUserId,
                ContentAuditActions.SocialChannelReaffirmed,
                channel,
                $"Reaffirmed the {channel.ChannelKey} post as revision {reaffirmed.RevisionNumber}.",
                ContentProposalStatus.NeedsReview),
            cancellationToken);
    }

    /// <summary>A channel's slot, opened for a new revision, with everything that revision is numbered from.</summary>
    private sealed record Slot(
        SocialPackage Package,
        bool IsNewPackage,
        SocialPackageChannel Channel,
        bool IsNewChannel,
        SocialRevision? Latest,
        SocialContextFacts Facts);

    /// <summary>
    /// Finds or makes the slot a new revision goes in, and refuses when the caller may not draft there.
    /// </summary>
    /// <remarks>
    /// The catalogue is asked only when the slot is new. A channel this package already holds keeps working
    /// after its key is retired: retiring a channel must not strand the posts already written for it.
    /// </remarks>
    private async Task<(Slot? Slot, OperationError? Refusal)> OpenSlotAsync(
        Guid contextId, string channelKey, CancellationToken cancellationToken)
    {
        var facts = await dataLayer.FindContextFactsAsync(contextId, cancellationToken);

        // One refusal for "no such context" and for "that context is another workspace's".
        if (facts is null)
        {
            return (null, NotFound());
        }

        var key = channelKey.Trim();
        var now = clock.UtcNow;
        var existing = await dataLayer.FindForUpdateAsync(contextId, cancellationToken);
        var package = existing ?? new SocialPackage
        {
            Id = Guid.NewGuid(),

            // From the resolved context, never from input: there is no field a caller could send one in.
            WorkspaceId = workspace.WorkspaceId,
            CreativeContextId = contextId,
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var channel = FindChannel(package, key);
        var from = channel?.Status;

        if (!Allowed(ContentProposalTransitions.Find(from, ContentProposalStatus.Proposed)))
        {
            return (null, Forbidden());
        }

        if (channel is not null)
        {
            var latest = await dataLayer.FindLatestRevisionAsync(channel.Id, cancellationToken);

            return (new Slot(package, existing is null, channel, IsNewChannel: false, latest, facts), null);
        }

        var known = channels.Find(key);

        if (known is not { IsActive: true })
        {
            return (null, OperationError.Validation(
                ContentErrorCodes.SocialChannelUnprocessable,
                CannotSave,
                [("channelKey", known is null
                    ? "That is not a channel."
                    : "That channel has been retired and cannot be newly chosen.")]));
        }

        channel = new SocialPackageChannel
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            SocialPackageId = package.Id,
            ChannelKey = key,
            Status = ContentProposalStatus.Proposed,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Not added to the package yet. The package may be tracked, and a slot hung on it now would ride along
        // on this scope's next save even if the caller goes on to refuse the write.
        return (new Slot(package, existing is null, channel, IsNewChannel: true, null, facts), null);
    }

    private SocialRevision NewRevision(Slot slot, string body, SocialLimitResult? limit, ContentRevisionSource source) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspace.WorkspaceId,
        SocialPackageChannelId = slot.Channel.Id,
        RevisionNumber = (slot.Latest?.RevisionNumber ?? 0) + 1,
        ParentRevisionId = slot.Latest?.Id,
        Source = source,

        // Stored exactly as given. Over-limit copy is flagged by its limit result and never trimmed here.
        Body = body,
        CharacterCount = limit?.CharacterCount,
        CharacterLimit = limit?.CharacterLimit,
        LimitStatus = limit?.Status ?? SocialLimitStatus.NotChecked,
        ChannelProfileVersion = limit?.ChannelProfileVersion,
        CreatedByMembershipId = workspace.MembershipId,
        CreatedAt = clock.UtcNow,
    };

    private async Task<OperationResult<SocialPackageServiceModel>> SaveDraftAsync(
        Slot slot, SocialRevision revision, OperationError onRefused, CancellationToken cancellationToken)
    {
        var now = revision.CreatedAt;

        if (slot.IsNewChannel)
        {
            slot.Package.Channels.Add(slot.Channel);
        }

        // New words await a decision whatever the slot was doing before. The accepted pointer is not touched:
        // a regeneration or an edit never takes away what the creator last accepted.
        slot.Channel.Status = ContentProposalStatus.Proposed;
        slot.Channel.StaleSince = null;
        slot.Channel.StaleReasons = ContentStaleReasons.None;
        slot.Channel.UpdatedAt = now;
        slot.Package.UpdatedAt = now;

        // Drafting is not audited; see ContentAuditActions.SocialChannelAccepted.
        return await dataLayer.SaveAsync(slot.Package, slot.IsNewPackage, revision, audit: null, cancellationToken) switch
        {
            SocialPackageWrite.Saved => await ReadAsync(slot.Package.CreativeContextId, cancellationToken),
            SocialPackageWrite.Stale => Failure(Stale()),
            _ => Failure(onRefused),
        };
    }

    private async Task<OperationResult<SocialPackageServiceModel>> SaveDecisionAsync(
        SocialPackage package, SocialRevision? revision, AuditEntry audit, CancellationToken cancellationToken) =>
        await dataLayer.SaveAsync(package, isNew: false, revision, audit, cancellationToken) is SocialPackageWrite.Saved
            ? await ReadAsync(package.CreativeContextId, cancellationToken)
            : Failure(Stale());

    private async Task<OperationResult<SocialPackageServiceModel>> ReadAsync(Guid contextId, CancellationToken cancellationToken)
    {
        var snapshot = await dataLayer.FindAsync(contextId, cancellationToken)
            ?? throw new InvalidOperationException("A post package that was just written could not be read back.");

        return OperationResult<SocialPackageServiceModel>.Success(ToServiceModel(snapshot));
    }

    private async Task<bool> IsPinStaleAsync(SocialRevision revision, CancellationToken cancellationToken)
    {
        if (revision.RecipeId is not { } recipeId || revision.RecipeVersionId is not { } versionId)
        {
            return false;
        }

        var pinned = await dataLayer.FindRecipeVersionNumberAsync(versionId, cancellationToken);
        var latest = await dataLayer.FindLatestRecipeVersionAsync(recipeId, cancellationToken);

        return pinned is { } pinnedNumber && latest is not null && ContentCurrency.IsStale(pinnedNumber, latest.VersionNumber);
    }

    // A rule with no minimum role is a system move, which no caller here may make.
    private bool Allowed(ContentProposalTransitionRule? rule) =>
        rule?.MinimumRole is { } minimum && workspace.Role >= minimum;

    private static SocialPackageChannel? FindChannel(SocialPackage? package, string channelKey) =>
        package?.Channels.FirstOrDefault(
            channel => string.Equals(channel.ChannelKey, channelKey.Trim(), StringComparison.Ordinal));

    private static SocialPackageServiceModel ToServiceModel(SocialPackageSnapshot snapshot)
    {
        var channels = snapshot.Package.Channels
            .OrderBy(channel => channel.CreatedAt)
            .ThenBy(channel => channel.ChannelKey, StringComparer.Ordinal)
            .Select(channel =>
            {
                var revisions = snapshot.Heads.Where(head => head.SocialPackageChannelId == channel.Id).ToList();
                var latest = revisions.MaxBy(head => head.RevisionNumber)
                    ?? throw new InvalidOperationException("A post channel has no revision.");
                var accepted = revisions.FirstOrDefault(head => head.Id == channel.AcceptedRevisionId);

                return new SocialChannelServiceModel(
                    channel.ChannelKey,
                    channel.Status,
                    ToServiceModel(latest),
                    accepted is null ? null : ToServiceModel(accepted),
                    IsCurrent(channel, accepted, snapshot),
                    channel.StaleSince,
                    channel.StaleReasons,
                    channel.UpdatedAt);
            })
            .ToList();

        return new SocialPackageServiceModel(
            snapshot.Package.Id,
            snapshot.Package.CreativeContextId,
            channels,
            snapshot.Package.CreatedAt,
            snapshot.Package.UpdatedAt);
    }

    // Decided from the pin, never from the status alone: the flag is set by a durable job after the recipe
    // commits, so an Accepted channel whose recipe has moved on must already read as not current.
    private static bool? IsCurrent(SocialPackageChannel channel, SocialRevision? accepted, SocialPackageSnapshot snapshot)
    {
        if (accepted is null)
        {
            return null;
        }

        if (channel.Status != ContentProposalStatus.Accepted)
        {
            return false;
        }

        if (accepted.RecipeId is not { } recipeId || accepted.RecipeVersionId is not { } versionId)
        {
            return true;
        }

        return snapshot.PinnedVersionNumbers.TryGetValue(versionId, out var pinned)
            && snapshot.LatestVersionNumbers.TryGetValue(recipeId, out var latest)
            && !ContentCurrency.IsStale(pinned, latest);
    }

    // Drops WorkspaceId and CreatedByMembershipId: the workspace is the route's, and a membership id never
    // leaves the server.
    private static SocialRevisionServiceModel ToServiceModel(SocialRevision revision) => new(
        revision.Id,
        revision.RevisionNumber,
        revision.ParentRevisionId,
        revision.Source,
        revision.Body,
        revision.CharacterCount,
        revision.CharacterLimit,
        revision.LimitStatus,
        revision.ChannelProfileVersion,
        revision.AiProposalId,
        revision.PromptTemplateId,
        revision.PromptTemplateVersion,
        revision.RecipeId,
        revision.RecipeVersionId,
        revision.BrandProfileRevisionId,
        revision.BrandStyleGuideVersionId,
        revision.CreatedAt);

    // State references only: the channel key, a revision number and statuses — never a word of the post.
    private static AuditEntry Audit(
        string actorUserId, string action, SocialPackageChannel channel, string summary, ContentProposalStatus before) => new(
        actorUserId,
        action,
        ContentAuditActions.SocialChannelResourceType,
        channel.Id.ToString("D"),
        CorrelationId(),
        summary,
        BeforeReference: before.ToString(),
        AfterReference: channel.Status.ToString());

    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private static OperationResult<SocialPackageServiceModel>? Invalid(IEnumerable<(string, string)> checks)
    {
        var failures = checks.ToList();

        return failures.Count == 0
            ? null
            : Failure(OperationError.Validation(ContentErrorCodes.SocialInvalid, CannotSave, failures));
    }

    private static OperationResult<SocialPackageServiceModel> Failure(OperationError error) =>
        OperationResult<SocialPackageServiceModel>.Failure(error);

    private static OperationError NotFound() => new(
        ContentErrorCodes.SocialNotFound,
        "That post could not be found.",
        new Dictionary<string, string[]>());

    private static OperationError Forbidden() => new(
        ContentErrorCodes.SocialForbidden,
        "You do not have permission to do that to posts in this workspace.",
        new Dictionary<string, string[]>());

    private static OperationError Stale() => new(
        ContentErrorCodes.SocialStale,
        "This post has changed since you opened it. Reload it and try again.",
        new Dictionary<string, string[]>());

    private static OperationError DecisionConflict(ContentProposalStatus status) => new(
        ContentErrorCodes.SocialDecisionConflict,
        status == ContentProposalStatus.NeedsReview
            ? "This post needs review because its recipe changed. Reaffirm it, edit it, or regenerate it."
            : "This post has nothing awaiting that decision.",
        new Dictionary<string, string[]>());

    // One sentence for every pin the keys can refuse, so a write cannot be used to ask what a neighbour owns.
    private static OperationError SourceRefused() => new(
        ContentErrorCodes.SocialSourceUnprocessable,
        "That post names a source that could not be found in this workspace.",
        new Dictionary<string, string[]>());
}
