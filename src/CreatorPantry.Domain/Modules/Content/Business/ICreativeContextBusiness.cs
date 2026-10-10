using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>
/// The creative context's rules: what may be named, what a stale edit is, and what an edit changes.
/// </summary>
public interface ICreativeContextBusiness
{
    Task<OperationResult<CreativeContextServiceModel>> CreateAsync(
        string actorUserId, CreateCreativeContextViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<CreativeContextServiceModel>> GetAsync(Guid contextId, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<CreativeContextSummaryServiceModel>> ListRecentAsync(
        CreativeContextListCriteria criteria, CancellationToken cancellationToken);

    Task<OperationResult<CreativeContextServiceModel>> PatchAsync(
        string actorUserId, Guid contextId, PatchCreativeContextViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<CreativeContextServiceModel>> AddReferenceAsync(
        Guid contextId, AddCreativeContextReferenceViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// The pictures one piece of work names, with what is known about what each shows (AF.6.6).
    /// </summary>
    /// <remarks>
    /// A read for a surface that shows a picture and offers to write about it: the ids its render route takes,
    /// whether the creator kept it or chose it, their own words about it, and a model's reading of it where
    /// one has been kept. A picture that no longer resolves in this workspace is left out rather than listed
    /// as unavailable — nothing can be shown of it, so there is nothing to offer.
    /// </remarks>
    Task<OperationResult<IReadOnlyList<CreativeContextPictureServiceModel>>> ListPicturesAsync(
        Guid contextId, CancellationToken cancellationToken);

    Task<OperationResult<CreativeContextServiceModel>> RemoveReferenceAsync(
        Guid contextId, Guid referenceId, string? expectedConcurrencyToken, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextBusiness"/>
internal sealed class CreativeContextBusiness(
    ICreativeContextDataLayer dataLayer,
    IContentChannelFacade channels,
    IWorkspaceWeeklyThemeFacade themes,
    IRecipeFacade recipes,
    IAiConceptLookupFacade concepts,
    IMediaAssetLookupFacade mediaAssets,
    IGeneratedImageLookupFacade generatedImages,
    IMediaPictureAnalysisFacade analyses,
    IPromptRecordFacade prompts,
    IWorkspaceContext workspace,
    IClock clock) : ICreativeContextBusiness
{
    private const string CannotSave = "That creative context could not be saved as described.";

    public async Task<OperationResult<CreativeContextServiceModel>> CreateAsync(
        string actorUserId, CreateCreativeContextViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for any caller that reaches Business without the facade's validator — a worker, a
        // plugin. The facade has already refused the same input; the rules live in one place so they cannot
        // disagree.
        if (Invalid(CreativeContextInputChecks.Create(model)) is { } invalid)
        {
            return invalid;
        }

        var channelKeys = CreativeContextInputChecks.NormalizeChannels(model.ChannelKeys);
        var themeKey = CreativeContextInputChecks.Normalize(model.WeeklyThemeKey);

        if (RefuseChannels(channelKeys, alreadyChosen: []) is { } channelRefusal)
        {
            return channelRefusal;
        }

        if (await RefuseThemeAsync(themeKey, cancellationToken) is { } themeRefusal)
        {
            return themeRefusal;
        }

        if (model.From is { } from && !await IsUsableAsync(from, cancellationToken))
        {
            return Failure(ReferenceRefused("from"));
        }

        var now = clock.UtcNow;
        var context = new CreativeContext
        {
            Id = Guid.NewGuid(),
            WorkingTitle = CreativeContextInputChecks.Normalize(model.WorkingTitle),
            PictureBrief = CreativeContextInputChecks.Normalize(model.PictureBrief),
            Day = model.Day,
            WeeklyThemeKey = themeKey,

            // From the resolved membership, never from input: there is no field a client could send one in.
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.Channels.AddRange(ChannelRows(context.Id, channelKeys));

        if (model.From is { } source)
        {
            context.References.Add(ReferenceRow(context.Id, source, sortOrder: 0, now));
        }

        var saved = await dataLayer.CreateAsync(
            context,
            Audit(
                actorUserId,
                ContentAuditActions.CreativeContextCreated,
                context.Id,
                $"Started a creative context with {context.Channels.Count} channel(s) and {context.References.Count} source(s)."),
            cancellationToken);

        if (!saved)
        {
            // The checks above passed a moment ago, so this is a source that stopped being nameable in
            // between or a transient fault. One refusal either way; the context was not created.
            return Failure(model.From is null ? NotSaved() : ReferenceRefused("from"));
        }

        return OperationResult<CreativeContextServiceModel>.Success(ToServiceModel(context));
    }

    public async Task<OperationResult<CreativeContextServiceModel>> GetAsync(
        Guid contextId, CancellationToken cancellationToken)
    {
        var context = await dataLayer.FindAsync(contextId, cancellationToken);

        // One refusal for "no such context" and for "that context is another workspace's", because by the
        // time the answer is null those were never two conditions here.
        return context is null
            ? Failure(NotFound())
            : OperationResult<CreativeContextServiceModel>.Success(ToServiceModel(context));
    }

    public async Task<OperationResult<IReadOnlyList<CreativeContextPictureServiceModel>>> ListPicturesAsync(
        Guid contextId, CancellationToken cancellationToken)
    {
        var context = await dataLayer.FindAsync(contextId, cancellationToken);

        if (context is null)
        {
            return OperationResult<IReadOnlyList<CreativeContextPictureServiceModel>>.Failure(NotFound());
        }

        var pictures = new List<CreativeContextPictureServiceModel>();

        foreach (var reference in context.References
            .Where(reference => reference.Kind is CreativeContextReferenceKind.DamAsset
                or CreativeContextReferenceKind.GeneratedImage)
            .OrderBy(reference => reference.SortOrder))
        {
            // Re-read through the module that owns the picture, inside this workspace — the same discipline
            // the package assembler follows, and for the same reason: what a reference said when it was added
            // is not evidence that the picture is still there to show.
            var picture = reference.Kind is CreativeContextReferenceKind.DamAsset
                ? await AssetPictureAsync(reference, cancellationToken)
                : await ImagePictureAsync(reference, cancellationToken);

            if (picture is not null)
            {
                pictures.Add(picture);
            }
        }

        return OperationResult<IReadOnlyList<CreativeContextPictureServiceModel>>.Success(pictures);
    }

    /// <summary>One library picture, or null when this workspace no longer has it.</summary>
    private async Task<CreativeContextPictureServiceModel?> AssetPictureAsync(
        CreativeContextReference reference, CancellationToken cancellationToken)
    {
        if (reference.MediaAssetId is not { } assetId)
        {
            return null;
        }

        var described = await mediaAssets.DescribeAsync(
            assetId, reference.MediaAssetVersionNumber, cancellationToken);

        if (described is null)
        {
            return null;
        }

        // Alt text belongs to the current pixels, so a pin to an earlier version is shown without it — the
        // rule the package assembler states: the creator may have replaced the file and rewritten the words.
        var altText = described.VersionNumber == described.CurrentVersionNumber ? described.AltText : null;

        var own = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();

        // The creator's own words win, and a reading is then not shown at all: showing one beside them would
        // tell a creator their posts know something the package never passes on.
        var reading = own is null ? await ReadingAsync(assetId, described.VersionNumber, null, cancellationToken) : null;

        return new CreativeContextPictureServiceModel(
            reference.Id,
            reference.Kind,
            reference.Purpose,
            assetId,
            described.VersionNumber,
            GeneratedImageId: null,
            own,
            reading,
            Grounding(own, reading));
    }

    /// <summary>One generated picture, or null when it is gone, declined or expired.</summary>
    private async Task<CreativeContextPictureServiceModel?> ImagePictureAsync(
        CreativeContextReference reference, CancellationToken cancellationToken)
    {
        if (reference.GeneratedImageId is not { } imageId
            || !await generatedImages.IsAvailableAsync(imageId, cancellationToken))
        {
            return null;
        }

        // No alt text, and there never will be: a generated picture is not in the library, so a reading is
        // the only thing that can say what it shows.
        var reading = await ReadingAsync(null, null, imageId, cancellationToken);

        return new CreativeContextPictureServiceModel(
            reference.Id,
            reference.Kind,
            reference.Purpose,
            MediaAssetId: null,
            MediaAssetVersionNumber: null,
            imageId,
            AltText: null,
            reading,
            Grounding(null, reading));
    }

    /// <summary>Which of the three a generation would be told about this picture.</summary>
    private static CreativeContextPictureDescriptionSource Grounding(
        string? altText, CreativeContextPictureReadingServiceModel? reading) =>
        altText is not null
            ? CreativeContextPictureDescriptionSource.CreatorAltText
            : reading is not null
                ? CreativeContextPictureDescriptionSource.StoredAnalysis
                : CreativeContextPictureDescriptionSource.NotDescribed;

    /// <summary>
    /// The part of a stored reading a task would be grounded on, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <strong>Filtered and capped by the package's own rule</strong>
    /// (<see cref="CreativeContextPackageSelection.GroundableObservations"/>), so this read cannot show a
    /// creator an observation no generation will ever see. Every observation keeps the confidence the reading
    /// gave it, because a surface showing one has to show that too.
    /// </remarks>
    private async Task<CreativeContextPictureReadingServiceModel?> ReadingAsync(
        Guid? assetId, int? versionNumber, Guid? generatedImageId, CancellationToken cancellationToken)
    {
        var found = assetId is { } asset && versionNumber is { } version
            ? await analyses.FindForAssetAsync(asset, version, cancellationToken)
            : generatedImageId is { } image
                ? await analyses.FindForGeneratedImageAsync(image, cancellationToken)
                : null;

        if (found is null)
        {
            return null;
        }

        var observations = found.Observations
            .Where(observation => !string.IsNullOrWhiteSpace(observation.Text))
            .Select(observation => new CreativeContextPictureObservation(
                observation.Aspect, observation.Text.Trim(), observation.Confidence))
            .ToList();

        var (kept, _) = CreativeContextPackageSelection.GroundableObservations(observations);

        return kept.Count == 0
            ? null
            : new CreativeContextPictureReadingServiceModel(kept, found.AnalyzedAt);
    }

    public async Task<CursorPageServiceModel<CreativeContextSummaryServiceModel>> ListRecentAsync(
        CreativeContextListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var (rows, hasMore) = await dataLayer.ListRecentAsync(criteria, cancellationToken);

        return PageBuilder.Build(rows, hasMore, criteria.Scope, row => new CreativeContextSummaryServiceModel(
            row.Id,
            row.WorkingTitle,
            row.ChannelKeys,
            row.Day,
            row.WeeklyThemeKey,
            row.ReferenceCount,
            row.UpdatedAt));
    }

    public async Task<OperationResult<CreativeContextServiceModel>> PatchAsync(
        string actorUserId, Guid contextId, PatchCreativeContextViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Invalid(CreativeContextInputChecks.Patch(model)) is { } invalid)
        {
            return invalid;
        }

        var context = await dataLayer.FindForUpdateAsync(contextId, cancellationToken);

        if (context is null)
        {
            return Failure(NotFound());
        }

        if (!CreativeContextConcurrencyToken.Matches(model.ExpectedConcurrencyToken, context.RowVersion))
        {
            return Failure(Stale());
        }

        var currentChannels = Ordered(context.Channels).Select(channel => channel.ChannelKey).ToList();

        var title = model.WorkingTitle.IsSubmitted
            ? CreativeContextInputChecks.Normalize(model.WorkingTitle.Value)
            : context.WorkingTitle;
        var brief = model.PictureBrief.IsSubmitted
            ? CreativeContextInputChecks.Normalize(model.PictureBrief.Value)
            : context.PictureBrief;
        var briefSource = model.BriefSource.Or(context.BriefSource);
        var workingBrief = model.WorkingBrief.IsSubmitted
            ? CreativeContextInputChecks.Normalize(model.WorkingBrief.Value)
            : context.WorkingBrief;
        var day = model.Day.Or(context.Day);
        var themeKey = model.WeeklyThemeKey.IsSubmitted
            ? CreativeContextInputChecks.Normalize(model.WeeklyThemeKey.Value)
            : context.WeeklyThemeKey;
        var channelKeys = model.ChannelKeys.IsSubmitted
            ? CreativeContextInputChecks.NormalizeChannels(model.ChannelKeys.Value)
            : currentChannels;
        var archived = model.Archived.IsSubmitted ? model.Archived.Value!.Value : context.ArchivedAt is not null;

        // Only what is newly chosen is checked. A channel or theme this context already holds stays, retired
        // or not: retiring one must not make every piece of work that used it uneditable.
        if (RefuseChannels(channelKeys, alreadyChosen: currentChannels) is { } channelRefusal)
        {
            return channelRefusal;
        }

        if (!string.Equals(themeKey, context.WeeklyThemeKey, StringComparison.Ordinal)
            && await RefuseThemeAsync(themeKey, cancellationToken) is { } themeRefusal)
        {
            return themeRefusal;
        }

        var channelsChanged = !channelKeys.SequenceEqual(currentChannels, StringComparer.Ordinal);
        var archiveChanged = archived != (context.ArchivedAt is not null);

        var changed = channelsChanged
            || archiveChanged
            || !string.Equals(title, context.WorkingTitle, StringComparison.Ordinal)
            || !string.Equals(brief, context.PictureBrief, StringComparison.Ordinal)
            || briefSource != context.BriefSource
            || !string.Equals(workingBrief, context.WorkingBrief, StringComparison.Ordinal)
            || day != context.Day
            || !string.Equals(themeKey, context.WeeklyThemeKey, StringComparison.Ordinal);

        if (!changed)
        {
            // Nothing to write, so nothing is written and the token does not move: an autosave that re-sends
            // what is already stored costs a read.
            return OperationResult<CreativeContextServiceModel>.Success(ToServiceModel(context));
        }

        var now = clock.UtcNow;

        context.WorkingTitle = title;
        context.PictureBrief = brief;
        context.BriefSource = briefSource;
        context.WorkingBrief = workingBrief;
        context.Day = day;
        context.WeeklyThemeKey = themeKey;

        if (channelsChanged)
        {
            // Replaced wholesale rather than reordered in place: both of this table's unique indexes are over
            // values a reorder swaps, and rows with fresh ids cannot collide with the ones being removed.
            context.Channels.Clear();
            context.Channels.AddRange(ChannelRows(context.Id, channelKeys));
        }

        AuditEntry? audit = null;

        if (archiveChanged)
        {
            context.ArchivedAt = archived ? now : null;
            audit = archived
                ? Audit(actorUserId, ContentAuditActions.CreativeContextArchived, context.Id, "Archived a creative context.")
                : Audit(actorUserId, ContentAuditActions.CreativeContextRestored, context.Id, "Restored a creative context.");
        }

        context.UpdatedAt = now;

        return await dataLayer.SaveAsync(context, audit, cancellationToken) is CreativeContextWrite.Saved
            ? OperationResult<CreativeContextServiceModel>.Success(ToServiceModel(context))
            : Failure(Stale());
    }

    public async Task<OperationResult<CreativeContextServiceModel>> AddReferenceAsync(
        Guid contextId, AddCreativeContextReferenceViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Invalid(CreativeContextInputChecks.Add(model)) is { } invalid)
        {
            return invalid;
        }

        var input = model.Reference!;
        var context = await dataLayer.FindForUpdateAsync(contextId, cancellationToken);

        if (context is null)
        {
            return Failure(NotFound());
        }

        if (!CreativeContextConcurrencyToken.Matches(model.ExpectedConcurrencyToken, context.RowVersion))
        {
            return Failure(Stale());
        }

        if (context.References.Count >= CreativeContextPolicy.MaxReferences)
        {
            return Failure(new OperationError(
                ContentErrorCodes.CreativeContextReferenceLimit,
                $"A creative context names at most {CreativeContextPolicy.MaxReferences} sources. Remove one first.",
                new Dictionary<string, string[]>()));
        }

        // Asked before the target is resolved, and safe to: it compares against rows this workspace already
        // holds on this context, so it says nothing about anything the caller could not already read.
        if (context.References.Any(existing => NamesSameSource(existing, input)))
        {
            return Failure(new OperationError(
                ContentErrorCodes.CreativeContextReferenceDuplicate,
                "This creative context already names that source.",
                new Dictionary<string, string[]>()));
        }

        if (!await IsUsableAsync(input, cancellationToken))
        {
            return Failure(ReferenceRefused("reference"));
        }

        var now = clock.UtcNow;
        var sortOrder = context.References.Count == 0 ? 0 : context.References.Max(existing => existing.SortOrder) + 1;

        context.References.Add(ReferenceRow(context.Id, input, sortOrder, now));

        // Moved so the root is written, which is what puts the row version into this save's WHERE clause.
        context.UpdatedAt = now;

        return await dataLayer.SaveAsync(context, audit: null, cancellationToken) switch
        {
            CreativeContextWrite.Saved => OperationResult<CreativeContextServiceModel>.Success(ToServiceModel(context)),
            CreativeContextWrite.Stale => Failure(Stale()),

            // The key refused what the facade lookup accepted a moment ago: the source stopped being
            // nameable in between. The same answer as if it never had been.
            _ => Failure(ReferenceRefused("reference")),
        };
    }

    public async Task<OperationResult<CreativeContextServiceModel>> RemoveReferenceAsync(
        Guid contextId, Guid referenceId, string? expectedConcurrencyToken, CancellationToken cancellationToken)
    {
        var context = await dataLayer.FindForUpdateAsync(contextId, cancellationToken);

        if (context is null)
        {
            return Failure(NotFound());
        }

        // Before looking for the reference: a caller working from an old read whose reference somebody else
        // already removed needs "re-read", not "no such thing".
        if (!CreativeContextConcurrencyToken.Matches(expectedConcurrencyToken, context.RowVersion))
        {
            return Failure(Stale());
        }

        var reference = context.References.FirstOrDefault(existing => existing.Id == referenceId);

        if (reference is null)
        {
            return Failure(NotFound());
        }

        // The usage goes; what it named stays. Positions are left with a gap rather than renumbered, since
        // order is what they record and a gap orders the same.
        context.References.Remove(reference);
        context.UpdatedAt = clock.UtcNow;

        return await dataLayer.SaveAsync(context, audit: null, cancellationToken) is CreativeContextWrite.Saved
            ? OperationResult<CreativeContextServiceModel>.Success(ToServiceModel(context))
            : Failure(Stale());
    }

    /// <summary>
    /// Whether the resolved workspace may point new work at this source, asked of the module that owns it.
    /// </summary>
    /// <remarks>
    /// One boolean for a source that does not exist, one that is another workspace's, and one that is archived,
    /// deleted, declined or expired — the caller turns all of them into the same refusal, so nothing here can
    /// be used to ask what a neighbour owns. The composite foreign keys remain the authority; this is what
    /// turns a storage exception into an answer a creator can act on.
    /// </remarks>
    private async Task<bool> IsUsableAsync(
        CreativeContextReferenceInputViewModel reference, CancellationToken cancellationToken)
    {
        switch (reference.Kind)
        {
            case CreativeContextReferenceKind.Recipe:
                var recipe = await recipes.GetDetailAsync(reference.RecipeId!.Value, cancellationToken);

                if (!recipe.Succeeded || recipe.Value!.Status is RecipeStatus.Archived)
                {
                    return false;
                }

                return reference.RecipeVersionId is not { } versionId
                    || (await recipes.GetSnapshotAsync(reference.RecipeId.Value, versionId, cancellationToken)).Succeeded;

            case CreativeContextReferenceKind.RecipeConcept:
                return await concepts.ExistsAsync(
                    reference.ConceptRequestId!.Value, reference.ConceptId!.Value, cancellationToken);

            case CreativeContextReferenceKind.DamAsset:
                // A soft-deleted asset answers AssetNotFound, so "deleted" needs no branch of its own.
                return await mediaAssets.ResolveLinkTargetAsync(
                    reference.MediaAssetId!.Value, reference.MediaAssetVersionNumber, cancellationToken)
                    is MediaAssetLinkTarget.Linkable;

            case CreativeContextReferenceKind.GeneratedImage:
                return await generatedImages.IsAvailableAsync(reference.GeneratedImageId!.Value, cancellationToken);

            case CreativeContextReferenceKind.PromptRecord:
                return (await prompts.GetDetailAsync(reference.PromptRecordId!.Value, cancellationToken)).Succeeded;

            default:
                // SocialPackage, and anything added to the enum before it is taught here: refused, never
                // stored unchecked.
                return false;
        }
    }

    /// <summary>The same notion of "one source" the table's filtered unique indexes hold.</summary>
    /// <summary>
    /// Whether the context already names this source for this purpose.
    /// </summary>
    /// <remarks>
    /// <strong>The purpose is part of the identity for a picture, and only for a picture.</strong> A work can
    /// take cues from a picture and also produce pictures of its own, so the same picture named once as a cue
    /// and once as a keeper is two facts rather than a repeat (AF.4.3) — which is why the two picture kinds
    /// are unique per purpose in the store too. Every other kind is named once however it is used: a recipe
    /// this work is about does not become a different source by being called something else.
    /// </remarks>
    private static bool NamesSameSource(
        CreativeContextReference existing, CreativeContextReferenceInputViewModel input) =>
        existing.Kind == input.Kind && input.Kind switch
        {
            CreativeContextReferenceKind.Recipe => existing.RecipeId == input.RecipeId,
            CreativeContextReferenceKind.RecipeConcept =>
                existing.ConceptRequestId == input.ConceptRequestId && existing.ConceptId == input.ConceptId,
            CreativeContextReferenceKind.DamAsset =>
                existing.MediaAssetId == input.MediaAssetId && SamePurpose(existing, input),
            CreativeContextReferenceKind.GeneratedImage =>
                existing.GeneratedImageId == input.GeneratedImageId && SamePurpose(existing, input),
            CreativeContextReferenceKind.PromptRecord => existing.PromptRecordId == input.PromptRecordId,
            _ => false,
        };

    private static bool SamePurpose(
        CreativeContextReference existing, CreativeContextReferenceInputViewModel input) =>
        existing.Purpose == (input.Purpose ?? CreativeContextReferencePurpose.Source);

    private OperationResult<CreativeContextServiceModel>? RefuseChannels(
        IReadOnlyList<string> channelKeys, IReadOnlyList<string> alreadyChosen)
    {
        var catalogue = channels.List();
        var failures = new List<(string, string)>();

        for (var index = 0; index < channelKeys.Count; index++)
        {
            var key = channelKeys[index];

            if (alreadyChosen.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            var channel = catalogue.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));

            if (channel is null)
            {
                failures.Add(($"channelKeys[{index}]", "That is not a channel."));
            }
            else if (!channel.IsActive)
            {
                failures.Add(($"channelKeys[{index}]", "That channel has been retired and cannot be newly chosen."));
            }
        }

        return failures.Count == 0
            ? null
            : Failure(OperationError.Validation(
                ContentErrorCodes.CreativeContextChannelUnprocessable, CannotSave, failures));
    }

    private async Task<OperationResult<CreativeContextServiceModel>?> RefuseThemeAsync(
        string? themeKey, CancellationToken cancellationToken)
    {
        if (themeKey is null)
        {
            return null;
        }

        // Through the weekly-theme facade, which reads inside the resolved workspace: another workspace's
        // theme with the same key is simply not this workspace's theme.
        var theme = await themes.FindAsync(themeKey, cancellationToken);

        if (theme.Succeeded && theme.Value!.RetiredAt is null)
        {
            return null;
        }

        return Failure(OperationError.Validation(
            ContentErrorCodes.CreativeContextThemeUnprocessable,
            CannotSave,
            [("weeklyThemeKey", theme.Succeeded
                ? "That theme has been retired and cannot be newly chosen."
                : "That is not one of this workspace's themes.")]));
    }

    private static IEnumerable<CreativeContextChannel> ChannelRows(Guid contextId, IReadOnlyList<string> channelKeys) =>
        channelKeys.Select((key, index) => new CreativeContextChannel
        {
            Id = Guid.NewGuid(),
            CreativeContextId = contextId,
            ChannelKey = key,
            SortOrder = index,
        });

    private static CreativeContextReference ReferenceRow(
        Guid contextId, CreativeContextReferenceInputViewModel input, int sortOrder, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            CreativeContextId = contextId,
            Kind = input.Kind!.Value,

            // Absent is Source: a caller that names a source without saying what it is for is naming
            // something the work draws on, which is every reference made before AF.4.3.
            Purpose = input.Purpose ?? CreativeContextReferencePurpose.Source,
            SortOrder = sortOrder,
            RecipeId = input.RecipeId,
            RecipeVersionId = input.RecipeVersionId,
            ConceptRequestId = input.ConceptRequestId,
            ConceptId = input.ConceptId,
            MediaAssetId = input.MediaAssetId,
            MediaAssetVersionNumber = input.MediaAssetVersionNumber,
            GeneratedImageId = input.GeneratedImageId,
            PromptRecordId = input.PromptRecordId,
            AddedAt = now,
        };

    private static IEnumerable<CreativeContextChannel> Ordered(IEnumerable<CreativeContextChannel> rows) =>
        rows.OrderBy(channel => channel.SortOrder);

    // Drops WorkspaceId and CreatedByMembershipId: the workspace is the route's, and a membership id never
    // leaves the server.
    private static CreativeContextServiceModel ToServiceModel(CreativeContext context) => new(
        context.Id,
        context.WorkingTitle,
        context.PictureBrief,
        context.BriefSource,
        context.WorkingBrief,
        [.. Ordered(context.Channels).Select(channel => channel.ChannelKey)],
        context.Day,
        context.WeeklyThemeKey,
        [.. context.References
            .OrderBy(reference => reference.SortOrder)
            .Select(reference => new CreativeContextReferenceServiceModel(
                reference.Id,
                reference.Kind,
                reference.Purpose,
                reference.SortOrder,
                reference.RecipeId,
                reference.RecipeVersionId,
                reference.ConceptRequestId,
                reference.ConceptId,
                reference.MediaAssetId,
                reference.MediaAssetVersionNumber,
                reference.GeneratedImageId,
                reference.PromptRecordId,
                reference.AddedAt))],
        context.CreatedAt,
        context.UpdatedAt,
        context.ArchivedAt,
        CreativeContextConcurrencyToken.From(context.RowVersion));

    // State references only: AuditLog has to stay safe to display, so ids and counts — never the creator's
    // title or the picture they described.
    private static AuditEntry Audit(string actorUserId, string action, Guid contextId, string summary) => new(
        actorUserId,
        action,
        ContentAuditActions.CreativeContextResourceType,
        contextId.ToString("D"),
        CorrelationId(),
        summary);

    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private static OperationResult<CreativeContextServiceModel>? Invalid(IEnumerable<(string, string)> checks)
    {
        var failures = checks.ToList();

        return failures.Count == 0
            ? null
            : Failure(OperationError.Validation(ContentErrorCodes.CreativeContextInvalid, CannotSave, failures));
    }

    private static OperationResult<CreativeContextServiceModel> Failure(OperationError error) =>
        OperationResult<CreativeContextServiceModel>.Failure(error);

    private static OperationError NotFound() => new(
        ContentErrorCodes.CreativeContextNotFound,
        "That creative context could not be found.",
        new Dictionary<string, string[]>());

    // A create has no token to be stale against, so it does not borrow that code: nothing here asks the
    // caller to re-read anything.
    private static OperationError NotSaved() => new(
        ContentErrorCodes.CreativeContextNotSaved,
        "The creative context could not be saved just then. Try again.",
        new Dictionary<string, string[]>());

    private static OperationError Stale() => new(
        ContentErrorCodes.CreativeContextStale,
        "This creative context has changed since you opened it. Reload it and try again.",
        new Dictionary<string, string[]>());

    // One sentence for every way a source can fail to be nameable, and the field it was sent in.
    private static OperationError ReferenceRefused(string field) => OperationError.Validation(
        ContentErrorCodes.CreativeContextReferenceUnprocessable,
        "That source cannot be added to a creative context.",
        [(field, "That source could not be found in this workspace, or can no longer be used.")]);
}
