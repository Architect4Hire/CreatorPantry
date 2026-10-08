using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Business;

public interface IBrandProfileBusiness
{
    /// <summary>The workspace's profile, or a failure carrying <see cref="BrandErrorCodes.ProfileNotFound"/>.</summary>
    Task<OperationResult<BrandProfileServiceModel>> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the profile as revision 1, or fails with <see cref="BrandErrorCodes.AlreadyExistsConflict"/> or a
    /// validation failure for a time zone that does not exist or a logo that is not in <paramref name="linkableAssetIds"/>.
    /// </summary>
    /// <param name="linkableAssetIds">
    /// The submitted asset ids the caller's workspace can link, as the facade resolved them through the Media
    /// module. Business decides what a link means and never reaches into another module to learn whether its
    /// target exists (backend.md); an id absent from this set is refused, whatever the reason it is absent.
    /// </param>
    Task<OperationResult<BrandProfileServiceModel>> CreateAsync(
        string actorUserId,
        CreateBrandProfileViewModel model,
        IReadOnlySet<Guid> linkableAssetIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a merge patch. Not found, conflict on a stale token, or validation failure on a time zone that
    /// does not exist or a newly linked logo that is not in <paramref name="linkableAssetIds"/>. A patch that
    /// changes nothing writes no revision and leaves the token valid.
    /// </summary>
    /// <param name="linkableAssetIds">See <see cref="CreateAsync"/>.</param>
    Task<OperationResult<BrandProfileServiceModel>> UpdateAsync(
        string actorUserId,
        UpdateBrandProfileViewModel model,
        IReadOnlySet<Guid> linkableAssetIds,
        CancellationToken cancellationToken);
}

internal sealed class BrandProfileBusiness(
    IBrandProfileDataLayer dataLayer,
    IWorkspaceContext workspace,
    ITimeZoneConverter zones,
    IContentChannelCatalog channels,
    IClock clock) : IBrandProfileBusiness
{
    public async Task<OperationResult<BrandProfileServiceModel>> GetAsync(CancellationToken cancellationToken)
    {
        var profile = await dataLayer.GetAsync(cancellationToken);

        return profile is null ? NotFound() : OperationResult<BrandProfileServiceModel>.Success(ToServiceModel(profile));
    }

    public async Task<OperationResult<BrandProfileServiceModel>> CreateAsync(
        string actorUserId,
        CreateBrandProfileViewModel model,
        IReadOnlySet<Guid> linkableAssetIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(linkableAssetIds);

        // A new profile holds no links yet, so every submitted logo has to be one the workspace can link.
        if (UnlinkableAssets(model.Assets, linkableAssetIds, alreadyLinked: new HashSet<Guid>()) is { } assetError)
        {
            return OperationResult<BrandProfileServiceModel>.Failure(assetError);
        }

        var zone = BrandProfileInputChecks.Normalize(model.TimeZoneId);
        if (UnknownZone(zone) is { } zoneError)
        {
            return OperationResult<BrandProfileServiceModel>.Failure(zoneError);
        }

        if (RetiredChannels(model.ChannelDefaults, alreadyStored: new HashSet<string>()) is { } retiredError)
        {
            return OperationResult<BrandProfileServiceModel>.Failure(retiredError);
        }

        var now = clock.UtcNow;
        var membershipId = workspace.MembershipId;

        // WorkspaceId is left unset throughout: stamping it is WorkspaceOwnershipInterceptor's job, from the
        // resolved context, and a value set here by hand would be the only place a client-shaped id could enter.
        var profile = new BrandProfile
        {
            Id = Guid.NewGuid(),
            BrandName = BrandProfileInputChecks.Normalize(model.BrandName)!,
            ShortDescription = BrandProfileInputChecks.Normalize(model.ShortDescription),
            DefaultAudience = BrandProfileInputChecks.Normalize(model.DefaultAudience),
            Locale = BrandProfileInputChecks.Normalize(model.Locale),
            TimeZoneId = zone,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = membershipId,
            UpdatedByMembershipId = membershipId,
        };

        ReplaceChannels(profile, model.ChannelDefaults);
        ReplaceLinks(profile, model.Links);
        ReplaceAssets(profile, model.Assets);

        var created = await dataLayer.CreateAsync(
            profile,
            NewRevision(profile, reason: null, membershipId, now),
            Audit(actorUserId, BrandAuditActions.Created, profile, before: null, "Created the brand profile."),
            cancellationToken);

        if (!created)
        {
            return OperationResult<BrandProfileServiceModel>.Failure(new OperationError(
                BrandErrorCodes.AlreadyExistsConflict,
                "This workspace already has a brand profile. Edit it instead.",
                new Dictionary<string, string[]>()));
        }

        return await GetAsync(cancellationToken);
    }

    public async Task<OperationResult<BrandProfileServiceModel>> UpdateAsync(
        string actorUserId,
        UpdateBrandProfileViewModel model,
        IReadOnlySet<Guid> linkableAssetIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(linkableAssetIds);

        if (model.TimeZoneId.TryGetSubmitted(out var submittedZone)
            && UnknownZone(BrandProfileInputChecks.Normalize(submittedZone)) is { } zoneError)
        {
            return OperationResult<BrandProfileServiceModel>.Failure(zoneError);
        }

        var profile = await dataLayer.GetForUpdateAsync(cancellationToken);
        if (profile is null)
        {
            return NotFound();
        }

        if (!BrandConcurrencyToken.Matches(model.ExpectedConcurrencyToken, profile.RowVersion))
        {
            return Conflict();
        }

        // After the token, as the channel check is: a refusal about a logo is answered from the profile the
        // caller actually read. A link the profile already holds is let through whatever has since happened to
        // its asset — the creator did not just choose it, and refusing would stop them saving anything else
        // until they dropped a logo they may not know has left the library. Only a newly named asset must be
        // one the workspace can link.
        if (model.Assets.TryGetSubmitted(out var submittedAssets)
            && UnlinkableAssets(
                submittedAssets, linkableAssetIds, profile.AssetLinks.Select(link => link.MediaAssetId).ToHashSet()) is { } assetError)
        {
            return OperationResult<BrandProfileServiceModel>.Failure(assetError);
        }

        if (model.ChannelDefaults.TryGetSubmitted(out var submittedChannels)
            && RetiredChannels(submittedChannels, profile.ChannelDefaults.Select(item => item.ChannelKey).ToHashSet(StringComparer.Ordinal)) is { } retiredError)
        {
            return OperationResult<BrandProfileServiceModel>.Failure(retiredError);
        }

        var before = BrandProfileSnapshot.From(profile).ToJson();
        var previousRevision = profile.Revision;

        Apply(profile, model);

        // An edit that changes nothing writes nothing: no revision, no audit row, and the token the caller
        // holds stays valid. Compared as serialized facts, so ordering and normalisation are part of the answer.
        if (BrandProfileSnapshot.From(profile).ToJson() == before)
        {
            return OperationResult<BrandProfileServiceModel>.Success(ToServiceModel(profile));
        }

        var now = clock.UtcNow;
        profile.Revision = previousRevision + 1;
        profile.UpdatedAt = now;
        profile.UpdatedByMembershipId = workspace.MembershipId;

        var saved = await dataLayer.UpdateAsync(
            profile,
            NewRevision(profile, BrandProfileInputChecks.Normalize(model.Reason), workspace.MembershipId, now),
            Audit(actorUserId, BrandAuditActions.Updated, profile, previousRevision, "Updated the brand profile."),
            cancellationToken);

        return saved ? await GetAsync(cancellationToken) : Conflict();
    }

    private static void Apply(BrandProfile profile, UpdateBrandProfileViewModel model)
    {
        if (model.BrandName.TryGetSubmitted(out var name))
        {
            profile.BrandName = BrandProfileInputChecks.Normalize(name)!;
        }

        if (model.ShortDescription.TryGetSubmitted(out var description))
        {
            profile.ShortDescription = BrandProfileInputChecks.Normalize(description);
        }

        if (model.DefaultAudience.TryGetSubmitted(out var audience))
        {
            profile.DefaultAudience = BrandProfileInputChecks.Normalize(audience);
        }

        if (model.Locale.TryGetSubmitted(out var locale))
        {
            profile.Locale = BrandProfileInputChecks.Normalize(locale);
        }

        if (model.TimeZoneId.TryGetSubmitted(out var zone))
        {
            profile.TimeZoneId = BrandProfileInputChecks.Normalize(zone);
        }

        // Lists replace as a whole, but only when they differ: replacing an identical list would delete and
        // re-insert every row to arrive where it started.
        if (model.ChannelDefaults.TryGetSubmitted(out var channels))
        {
            var next = (channels ?? []).Select(item => BrandProfileInputChecks.Normalize(item!.ChannelKey)!).ToList();
            if (!profile.ChannelDefaults.OrderBy(item => item.SortOrder).Select(item => item.ChannelKey).SequenceEqual(next))
            {
                ReplaceChannels(profile, channels);
            }
        }

        if (model.Links.TryGetSubmitted(out var links))
        {
            var next = (links ?? [])
                .Select(item => new BrandLinkSnapshot(item!.Kind!.Value, BrandProfileInputChecks.Normalize(item.Url)!, BrandProfileInputChecks.Normalize(item.Label)))
                .ToList();
            var current = profile.Links.OrderBy(link => link.SortOrder)
                .Select(link => new BrandLinkSnapshot(link.Kind, link.Url, link.Label));
            if (!current.SequenceEqual(next))
            {
                ReplaceLinks(profile, links);
            }
        }

        if (model.Assets.TryGetSubmitted(out var assets))
        {
            var next = (assets ?? [])
                .Select(item => new BrandAssetSnapshot(item!.MediaAssetId!.Value, item.Role!.Value))
                .ToList();
            var current = profile.AssetLinks.OrderBy(link => link.SortOrder)
                .Select(link => new BrandAssetSnapshot(link.MediaAssetId, link.Role));
            if (!current.SequenceEqual(next))
            {
                ReplaceAssets(profile, assets);
            }
        }
    }

    /// <summary>
    /// The refusal for any submitted logo this workspace cannot link, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// The composite key on <c>BrandAssetLinks</c> already stops a link to another workspace's asset. It cannot
    /// see <c>DeletedAt</c>, so an asset removed from the library would pass it; and a key violation is an
    /// exception nobody reads. This is the rule that answers in words, and answers the three cases alike.
    /// </remarks>
    private static OperationError? UnlinkableAssets(
        IReadOnlyList<BrandAssetInput?>? submitted, IReadOnlySet<Guid> linkable, IReadOnlySet<Guid> alreadyLinked)
    {
        var refused = (submitted ?? [])
            .Select((item, position) => (Id: item?.MediaAssetId, position))
            .Where(entry => entry.Id is { } id && !linkable.Contains(id) && !alreadyLinked.Contains(id))
            .Select(entry => entry.position)
            .ToList();

        return refused.Count == 0 ? null : BrandProfileErrors.AssetsUnprocessable(refused);
    }

    private static void ReplaceChannels(BrandProfile profile, IReadOnlyList<BrandChannelDefaultInput?>? channels)
    {
        profile.ChannelDefaults.Clear();
        foreach (var (item, index) in (channels ?? []).Select((item, index) => (item, index)))
        {
            profile.ChannelDefaults.Add(new BrandChannelDefault
            {
                Id = Guid.NewGuid(),

                // Set rather than left for the ownership interceptor to stamp: on an edit the profile is a tracked
                // row with a real WorkspaceId, which is part of this child's foreign key, and EF refuses to fill
                // a key column by fixup on an entity that is still forming. On a create it is still empty and the
                // interceptor stamps it, as it does for the profile itself.
                WorkspaceId = profile.WorkspaceId,
                BrandProfileId = profile.Id,
                ChannelKey = BrandProfileInputChecks.Normalize(item!.ChannelKey)!,
                SortOrder = index,
            });
        }
    }

    private static void ReplaceLinks(BrandProfile profile, IReadOnlyList<BrandLinkInput?>? links)
    {
        profile.Links.Clear();
        foreach (var (item, index) in (links ?? []).Select((item, index) => (item, index)))
        {
            profile.Links.Add(new BrandLink
            {
                Id = Guid.NewGuid(),

                // Set rather than left for the ownership interceptor to stamp: on an edit the profile is a tracked
                // row with a real WorkspaceId, which is part of this child's foreign key, and EF refuses to fill
                // a key column by fixup on an entity that is still forming. On a create it is still empty and the
                // interceptor stamps it, as it does for the profile itself.
                WorkspaceId = profile.WorkspaceId,
                BrandProfileId = profile.Id,
                Kind = item!.Kind!.Value,
                Url = BrandProfileInputChecks.Normalize(item.Url)!,
                Label = BrandProfileInputChecks.Normalize(item.Label),
                SortOrder = index,
            });
        }
    }

    private static void ReplaceAssets(BrandProfile profile, IReadOnlyList<BrandAssetInput?>? assets)
    {
        profile.AssetLinks.Clear();
        foreach (var (item, index) in (assets ?? []).Select((item, index) => (item, index)))
        {
            profile.AssetLinks.Add(new BrandAssetLink
            {
                Id = Guid.NewGuid(),

                // Set rather than left for the ownership interceptor to stamp: on an edit the profile is a tracked
                // row with a real WorkspaceId, which is part of this child's foreign key, and EF refuses to fill
                // a key column by fixup on an entity that is still forming. On a create it is still empty and the
                // interceptor stamps it, as it does for the profile itself.
                WorkspaceId = profile.WorkspaceId,
                BrandProfileId = profile.Id,
                MediaAssetId = item!.MediaAssetId!.Value,
                Role = item.Role!.Value,
                SortOrder = index,
            });
        }
    }

    private BrandProfileRevision NewRevision(BrandProfile profile, string? reason, Guid membershipId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        BrandProfileId = profile.Id,
        Revision = profile.Revision,
        SchemaVersion = BrandProfileSnapshot.CurrentSchemaVersion,
        Document = BrandProfileSnapshot.From(profile).ToJson(),
        Reason = reason,
        ChangedByMembershipId = membershipId,
        CreatedAt = now,
    };

    // State references only: AuditLog requires these to stay safe to display, so revision numbers and never the
    // creator's words, and in particular never the reason.
    private static AuditEntry Audit(
        string actorUserId, string action, BrandProfile profile, int? before, string summary) => new(
            actorUserId,
            action,
            BrandAuditActions.ResourceType,
            profile.Id.ToString("D"),
            CorrelationId(),
            summary,
            BeforeReference: before?.ToString(),
            AfterReference: profile.Revision.ToString());

    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    /// <summary>
    /// A retired channel may stay where it is already chosen, so an unrelated edit never fails because of a
    /// channel retired after the profile was written, but it cannot be newly chosen.
    /// </summary>
    private OperationError? RetiredChannels(IReadOnlyList<BrandChannelDefaultInput?>? submitted, ISet<string> alreadyStored)
    {
        var failures = new List<(string Field, string Error)>();

        for (var index = 0; index < (submitted?.Count ?? 0); index++)
        {
            var key = BrandProfileInputChecks.Normalize(submitted![index]?.ChannelKey);
            if (key is not null && channels.Find(key) is { IsActive: false } && !alreadyStored.Contains(key))
            {
                failures.Add(($"{nameof(CreateBrandProfileViewModel.ChannelDefaults)}[{index}].ChannelKey", "That channel is no longer available."));
            }
        }

        return failures.Count == 0
            ? null
            : OperationError.Validation(BrandErrorCodes.InvalidRequest, "The brand profile could not be saved.", failures);
    }

    private OperationError? UnknownZone(string? zone) =>
        zone is not null && !zones.IsValidZone(zone)
            ? OperationError.Validation(
                BrandErrorCodes.InvalidRequest,
                "The brand profile could not be saved.",
                [(nameof(CreateBrandProfileViewModel.TimeZoneId), "That is not a time zone we recognise. Use an identifier such as America/Chicago.")])
            : null;

    private static OperationResult<BrandProfileServiceModel> NotFound() =>
        OperationResult<BrandProfileServiceModel>.Failure(new OperationError(
            BrandErrorCodes.ProfileNotFound,
            "This workspace has no brand profile yet.",
            new Dictionary<string, string[]>()));

    private static OperationResult<BrandProfileServiceModel> Conflict() =>
        OperationResult<BrandProfileServiceModel>.Failure(new OperationError(
            BrandErrorCodes.Conflict,
            "The brand profile changed since you loaded it. Reload it and apply your edit again.",
            new Dictionary<string, string[]>()));

    private static BrandProfileServiceModel ToServiceModel(BrandProfile profile) => new(
        profile.Id,
        profile.BrandName,
        profile.ShortDescription,
        profile.DefaultAudience,
        profile.Locale,
        profile.TimeZoneId,
        [.. profile.ChannelDefaults.OrderBy(item => item.SortOrder).Select(item => new BrandChannelDefaultServiceModel(item.ChannelKey))],
        [.. profile.Links.OrderBy(link => link.SortOrder).Select(link => new BrandLinkServiceModel(link.Kind, link.Url, link.Label))],
        [.. profile.AssetLinks.OrderBy(link => link.SortOrder).Select(link => new BrandAssetServiceModel(link.MediaAssetId, link.Role))],
        profile.Revision,
        profile.CreatedAt,
        profile.UpdatedAt,
        BrandConcurrencyToken.From(profile.RowVersion));
}
