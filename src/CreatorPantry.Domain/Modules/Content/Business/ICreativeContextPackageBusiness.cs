using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>
/// Turns a creative context into the bounded grounding package one generation task may be shown (AF.1.5).
/// </summary>
public interface ICreativeContextPackageBusiness
{
    /// <summary>
    /// Assembles the package for one task, re-reading every source through the module that owns it.
    /// </summary>
    /// <remarks>
    /// Fails only when the context itself is not this workspace's to read. A source that no longer resolves
    /// is never a failure: it is dropped and named in the package.
    /// </remarks>
    Task<OperationResult<CreativeContextPackage>> AssembleAsync(
        Guid contextId, AiTaskType taskType, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICreativeContextPackageBusiness"/>
/// <remarks>
/// <para>
/// <strong>No model is called here, and nothing is guessed.</strong> Every value in a package was read, a
/// moment ago, from a record the resolved workspace may read. What a reference says about its target at the
/// time it was added is not trusted: the target is read again, and one that is gone contributes nothing.
/// </para>
/// <para>
/// <strong>The workspace is never an input.</strong> There is no parameter for one. It reaches every read
/// through the resolved context each facade below already uses — which matters more here than on an HTTP
/// route, because this runs in the worker, where no route policy stands in the way.
/// </para>
/// </remarks>
internal sealed class CreativeContextPackageBusiness(
    ICreativeContextDataLayer dataLayer,
    IContentChannelFacade channels,
    IWorkspaceWeeklyThemeFacade themes,
    IRecipeFacade recipes,
    IAiConceptLookupFacade concepts,
    IMediaAssetLookupFacade mediaAssets,
    IGeneratedImageLookupFacade generatedImages,
    IMediaPictureAnalysisFacade analyses,
    IPromptRecordFacade prompts,
    IClock clock) : ICreativeContextPackageBusiness
{
    /// <summary>One resolved source, with what it costs, in the order the context names it.</summary>
    private sealed record Resolved(CreativeContextReference Reference, object Entry, int Cost);

    public async Task<OperationResult<CreativeContextPackage>> AssembleAsync(
        Guid contextId, AiTaskType taskType, CancellationToken cancellationToken)
    {
        var context = await dataLayer.FindAsync(contextId, cancellationToken);

        // One refusal for "no such context" and "another workspace's context": the query filter means the
        // server never saw the other row.
        if (context is null)
        {
            return OperationResult<CreativeContextPackage>.Failure(new OperationError(
                ContentErrorCodes.CreativeContextNotFound,
                "That creative context could not be found.",
                new Dictionary<string, string[]>()));
        }

        var allowed = CreativeContextPackageSelection.SectionsFor(taskType);

        // A task the table grounds in nothing gets nothing — not the title, not a channel, not the context's
        // version. An early return rather than a set of empty reads below, because it is the only form of
        // that guarantee a later edit cannot undo by populating one more field.
        if (allowed is CreativeContextSections.None)
        {
            return OperationResult<CreativeContextPackage>.Success(Empty(context, taskType));
        }

        var omissions = new SortedSet<CreativeContextOmission>();
        var dropped = new List<CreativeContextDrop>();

        var words = ReadWords(context, allowed);
        var channelEntries = allowed.HasFlag(CreativeContextSections.Channels) ? ReadChannels(context) : [];
        var day = allowed.HasFlag(CreativeContextSections.Day)
            ? await ReadDayAsync(context, omissions, cancellationToken)
            : null;

        var resolved = new List<Resolved>();
        var counts = new Dictionary<CreativeContextSections, int>();

        foreach (var reference in context.References.OrderBy(item => item.SortOrder).ThenBy(item => item.Id))
        {
            var section = SectionOf(reference.Kind);

            if (section is CreativeContextSections.None || !allowed.HasFlag(section))
            {
                dropped.Add(Drop(reference, CreativeContextDropReason.NotUsedByTask));
                continue;
            }

            // The cap is checked before the read: a source there is no room for is not worth a query, and
            // the answer must not depend on whether a source nobody will use happens to still exist.
            if (counts.GetValueOrDefault(section) >= CapFor(section))
            {
                dropped.Add(Drop(reference, CreativeContextDropReason.OverCap));
                continue;
            }

            var entry = await ResolveAsync(reference, omissions, cancellationToken);

            if (entry is null)
            {
                dropped.Add(Drop(reference, CreativeContextDropReason.Unavailable));
                continue;
            }

            counts[section] = counts.GetValueOrDefault(section) + 1;
            resolved.Add(entry);
        }

        // The creator's words, the channels and the day are never dropped: they are small, bounded by their
        // own columns, and a package without them is not about this piece of work. Sources go from the end,
        // whole, until what is left fits.
        var fixedCost = CreativeContextPackageSelection.Estimate(words)
            + CreativeContextPackageSelection.Estimate(channelEntries)
            + CreativeContextPackageSelection.Estimate(day);

        while (resolved.Count > 0
            && fixedCost + resolved.Sum(item => item.Cost) > CreativeContextPackageSelection.MaxEstimatedTokens)
        {
            var last = resolved[^1];
            resolved.RemoveAt(resolved.Count - 1);
            dropped.Add(Drop(last.Reference, CreativeContextDropReason.OverBudget));
        }

        var recipeEntries = resolved.Select(item => item.Entry).OfType<CreativeContextRecipeEntry>().ToList();
        var conceptEntries = resolved.Select(item => item.Entry).OfType<CreativeContextConceptEntry>().ToList();
        var pictureEntries = resolved.Select(item => item.Entry).OfType<CreativeContextPictureEntry>().ToList();
        var promptEntries = resolved.Select(item => item.Entry).OfType<CreativeContextPromptEntry>().ToList();

        var contextVersion = CreativeContextConcurrencyToken.From(context.RowVersion);

        // In the context's own order, so two assemblies of the same context report the same list.
        List<CreativeContextDrop> reported =
        [
            .. dropped
                .OrderBy(drop => context.References.First(reference => reference.Id == drop.ReferenceId).SortOrder)
                .ThenBy(drop => drop.ReferenceId),
        ];

        return OperationResult<CreativeContextPackage>.Success(new CreativeContextPackage(

            // The row's own workspace, read with it. Not a caller's claim, and not the ambient context's: this
            // is the workspace the content came from, which is the thing the envelope has to be told.
            context.WorkspaceId,
            taskType,
            context.Id,
            contextVersion,
            words,
            channelEntries,
            day,
            recipeEntries,
            conceptEntries,
            pictureEntries,
            promptEntries,
            reported,
            [.. omissions],
            fixedCost + resolved.Sum(item => item.Cost),
            CreativeContextPackageSelection.Checksum(
                taskType,
                context.Id,
                contextVersion,
                words,
                channelEntries,
                day,
                recipeEntries,
                conceptEntries,
                pictureEntries,
                promptEntries,
                reported),
            clock.UtcNow));
    }

    private CreativeContextPackage Empty(CreativeContext context, AiTaskType taskType) => new(
        context.WorkspaceId,
        taskType,
        context.Id,
        ContextVersion: null,
        Words: null,
        Channels: [],
        Day: null,
        Recipes: [],
        Concepts: [],
        Pictures: [],
        Prompts: [],
        Dropped: [],
        Omissions: [],
        EstimatedTokens: 0,
        Checksum: CreativeContextPackageSelection.Checksum(taskType, context.Id, null, null, [], null, [], [], [], [], []),
        AssembledAt: clock.UtcNow);

    private static CreativeContextWords? ReadWords(CreativeContext context, CreativeContextSections allowed)
    {
        var title = allowed.HasFlag(CreativeContextSections.WorkingTitle) ? context.WorkingTitle : null;
        var brief = allowed.HasFlag(CreativeContextSections.PictureBrief) ? context.PictureBrief : null;

        // Both are bounded by their own columns, so there is nothing to cap here and nothing is ever cut.
        return title is null && brief is null ? null : new CreativeContextWords(title, brief);
    }

    private List<CreativeContextChannelEntry> ReadChannels(CreativeContext context)
    {
        var catalogue = channels.List();

        // A retired channel the context already holds still has a name and is still what the piece is for. A
        // key the catalogue has never had names nothing, and is left out rather than passed on as if it did.
        return
        [
            .. context.Channels
                .OrderBy(channel => channel.SortOrder)
                .Select(channel => catalogue.FirstOrDefault(
                    candidate => string.Equals(candidate.Key, channel.ChannelKey, StringComparison.Ordinal)))
                .Where(channel => channel is not null)
                .Select(channel => new CreativeContextChannelEntry(channel!.Key, channel.DisplayName)),
        ];
    }

    private async Task<CreativeContextDayEntry?> ReadDayAsync(
        CreativeContext context, SortedSet<CreativeContextOmission> omissions, CancellationToken cancellationToken)
    {
        if (context.WeeklyThemeKey is not { } themeKey)
        {
            return context.Day is null ? null : new CreativeContextDayEntry(context.Day, null, null, null, null);
        }

        // Through the weekly-theme facade, which reads inside the resolved workspace: a neighbour's theme
        // under the same key is simply not this workspace's theme.
        var found = await themes.FindAsync(themeKey, cancellationToken);

        if (!found.Succeeded)
        {
            // The key names nothing here any more. The day still stands; the theme is reported, not invented.
            omissions.Add(CreativeContextOmission.ThemeUnavailable);

            return context.Day is null ? null : new CreativeContextDayEntry(context.Day, null, null, null, null);
        }

        var theme = found.Value!;
        var description = theme.Description?.Trim();

        if (description is { Length: > CreativeContextPackageSelection.MaxThemeDescriptionLength })
        {
            omissions.Add(CreativeContextOmission.ThemeDescriptionOverCap);
            description = null;
        }

        // A retired theme is still passed on: the creator chose it for this piece and retirement is about what
        // can be newly chosen, not about what existing work means.
        return new CreativeContextDayEntry(
            context.Day,
            theme.Key,
            theme.DisplayName,
            string.IsNullOrEmpty(description) ? null : description,
            theme.Revision);
    }

    /// <summary>
    /// The stored reading of one picture, as this package carries it; null when nobody has read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Mapped here, at the boundary.</strong> The media module's observation type does not travel into
    /// a package: a package crosses to the AI module, and that module should not have to name a media type to
    /// read one label off an observation.
    /// </para>
    /// <para>
    /// <strong>Only what the reading was clear about, and each with its confidence.</strong> The filtering is
    /// <see cref="CreativeContextPackageSelection.GroundableObservations"/>'s, which says why; the label travels
    /// with the text it belongs to because a reading that lost its labels reads as a set of facts.
    /// </para>
    /// <para>
    /// <strong>A reading that grounds nothing is no reading at all:</strong> an empty list reads as never
    /// having looked, rather than as a picture described as featureless. The media module refuses to keep a
    /// blank observation, so the blank-text guard below is against a reading stored some other way rather than
    /// a state this codebase can reach — which is why no test sets one up.
    /// </para>
    /// </remarks>
    private async Task<CreativeContextPictureReading?> ReadingAsync(
        Guid? assetId,
        int? versionNumber,
        Guid? generatedImageId,
        SortedSet<CreativeContextOmission> omissions,
        CancellationToken cancellationToken)
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
                Trim(observation.Aspect),
                observation.Text.Trim(),
                Trim(observation.Confidence)))
            .ToList();

        var (kept, omitted) = CreativeContextPackageSelection.GroundableObservations(observations);

        if (omitted)
        {
            omissions.Add(CreativeContextOmission.PictureAnalysisOverCap);
        }

        return kept.Count == 0 ? null : new CreativeContextPictureReading(kept, found.AnalyzedAt);
    }

    /// <summary>One label, bounded. A label is a word for an aspect or a confidence, never a description.</summary>
    private static string Trim(string label) =>
        label.Length > CreativeContextPackageSelection.MaxPictureObservationLabelLength
            ? label[..CreativeContextPackageSelection.MaxPictureObservationLabelLength]
            : label;

    private async Task<Resolved?> ResolveAsync(
        CreativeContextReference reference,
        SortedSet<CreativeContextOmission> omissions,
        CancellationToken cancellationToken)
    {
        switch (reference.Kind)
        {
            case CreativeContextReferenceKind.Recipe when reference.RecipeId is { } recipeId:
            {
                var recipe = await ReadRecipeAsync(reference, recipeId, omissions, cancellationToken);

                return recipe is null
                    ? null
                    : new Resolved(reference, recipe, CreativeContextPackageSelection.Estimate(recipe));
            }

            case CreativeContextReferenceKind.RecipeConcept
                when reference.ConceptRequestId is { } requestId && reference.ConceptId is { } conceptId:
            {
                var found = await concepts.FindAsync(requestId, conceptId, cancellationToken);
                var title = found?.Title.Trim();

                // A concept is its title. One with none, or one too long to carry whole, is not carried.
                if (string.IsNullOrEmpty(title) || title.Length > CreativeContextPackageSelection.MaxConceptTitleLength)
                {
                    return null;
                }

                var summary = found!.Summary?.Trim();

                if (summary is { Length: > CreativeContextPackageSelection.MaxConceptSummaryLength })
                {
                    omissions.Add(CreativeContextOmission.ConceptSummaryOverCap);
                    summary = null;
                }

                var concept = new CreativeContextConceptEntry(
                    reference.Id, requestId, conceptId, title, string.IsNullOrEmpty(summary) ? null : summary);

                return new Resolved(reference, concept, CreativeContextPackageSelection.Estimate(concept));
            }

            case CreativeContextReferenceKind.DamAsset when reference.MediaAssetId is { } assetId:
            {
                var described = await mediaAssets.DescribeAsync(
                    assetId, reference.MediaAssetVersionNumber, cancellationToken);

                if (described is null)
                {
                    return null;
                }

                // Alt text is one value on the asset, not one per version, so it is only known to describe the
                // current pixels. A pin to an earlier version may be to a picture the words were never about —
                // the creator replaced the file and rewrote the description — and attributing them to it would
                // be describing pixels from a caption written for different ones. Such a picture stands as not
                // described.
                var altText = described.VersionNumber == described.CurrentVersionNumber
                    ? described.AltText?.Trim()
                    : null;

                if (altText is { Length: > CreativeContextPackageSelection.MaxAltTextLength })
                {
                    // Not cut short: the end of a description is as likely as its start to be the part that
                    // says what the picture is of. Reported, and the picture stands as not described.
                    omissions.Add(CreativeContextOmission.AltTextOverCap);
                    altText = null;
                }

                if (!string.IsNullOrEmpty(altText))
                {
                    var described0 = new CreativeContextPictureEntry(
                        reference.Id,
                        reference.Kind,
                        assetId,
                        described.VersionNumber,
                        CreativeContextPictureDescriptionSource.CreatorAltText,
                        altText);

                    return new Resolved(reference, described0, CreativeContextPackageSelection.Estimate(described0));
                }

                // No words of the creator's own, so a stored reading of these very pixels fills the gap
                // (AF.6.6). Asked for the pinned version, because a reading belongs to the bytes it was made
                // from — the same reason alt text from a later version is not attributed to an earlier one.
                var read = await ReadingAsync(assetId, described.VersionNumber, null, omissions, cancellationToken);

                var picture = read is null
                    ? new CreativeContextPictureEntry(
                        reference.Id,
                        reference.Kind,
                        assetId,
                        described.VersionNumber,
                        CreativeContextPictureDescriptionSource.NotDescribed,
                        Description: null)
                    : new CreativeContextPictureEntry(
                        reference.Id,
                        reference.Kind,
                        assetId,
                        described.VersionNumber,
                        CreativeContextPictureDescriptionSource.StoredAnalysis,
                        Description: null,
                        read);

                return new Resolved(reference, picture, CreativeContextPackageSelection.Estimate(picture));
            }

            case CreativeContextReferenceKind.GeneratedImage when reference.GeneratedImageId is { } imageId:
            {
                if (!await generatedImages.IsAvailableAsync(imageId, cancellationToken))
                {
                    return null;
                }

                // A generated image is never in the library, so it has no alt text and a stored reading is
                // its only possible description. Where there is none, nothing is said about these pixels —
                // and in particular not the prompt that made them, which describes what was asked for rather
                // than what came back (ai.md, media.md).
                var read = await ReadingAsync(null, null, imageId, omissions, cancellationToken);

                var picture = read is null
                    ? new CreativeContextPictureEntry(
                        reference.Id,
                        reference.Kind,
                        imageId,
                        VersionNumber: null,
                        CreativeContextPictureDescriptionSource.NotDescribed,
                        Description: null)
                    : new CreativeContextPictureEntry(
                        reference.Id,
                        reference.Kind,
                        imageId,
                        VersionNumber: null,
                        CreativeContextPictureDescriptionSource.StoredAnalysis,
                        Description: null,
                        read);

                return new Resolved(reference, picture, CreativeContextPackageSelection.Estimate(picture));
            }

            case CreativeContextReferenceKind.PromptRecord when reference.PromptRecordId is { } promptId:
            {
                var found = await prompts.GetDetailAsync(promptId, cancellationToken);

                if (!found.Succeeded)
                {
                    return null;
                }

                // The authoritative text — what the creator shipped — never the model's draft beside it.
                var prompt = new CreativeContextPromptEntry(reference.Id, promptId, found.Value!.Text);

                return new Resolved(reference, prompt, CreativeContextPackageSelection.Estimate(prompt));
            }

            default:
                return null;
        }
    }

    private async Task<CreativeContextRecipeEntry?> ReadRecipeAsync(
        CreativeContextReference reference,
        Guid recipeId,
        SortedSet<CreativeContextOmission> omissions,
        CancellationToken cancellationToken)
    {
        var detail = await recipes.GetDetailAsync(recipeId, cancellationToken);

        if (!detail.Succeeded || detail.Value!.Status is RecipeStatus.Archived)
        {
            return null;
        }

        // The version the creator pinned, or — when they named the recipe alone — whichever is current now,
        // recorded below so the package still says exactly what it read. Never the live draft: a draft can
        // change between this read and the generation, and a version cannot.
        var versionId = reference.RecipeVersionId ?? detail.Value.CurrentVersion?.Id;

        if (versionId is not { } pinned)
        {
            return null;
        }

        var snapshot = await recipes.GetSnapshotAsync(recipeId, pinned, cancellationToken);

        if (!snapshot.Succeeded)
        {
            return null;
        }

        var document = snapshot.Value!.Document;

        // The creator's own lines, as entered and in their order. Display text only: a normalised quantity or
        // a matched ingredient name would be the system's reading of the line, not the line.
        var (ingredients, omittedIngredients) = CreativeContextPackageSelection.KeepWholeLines(
            document.IngredientGroups
                .OrderBy(group => group.SortOrder)
                .SelectMany(group => group.Ingredients.OrderBy(line => line.SortOrder))
                .Select(line => line.DisplayText),
            CreativeContextPackageSelection.MaxIngredientLines,
            CreativeContextPackageSelection.MaxIngredientLineLength);

        var (steps, omittedSteps) = CreativeContextPackageSelection.KeepWholeLines(
            document.InstructionGroups
                .OrderBy(group => group.SortOrder)
                .SelectMany(group => group.Steps.OrderBy(step => step.SortOrder))
                .Select(step => step.Text),
            CreativeContextPackageSelection.MaxSteps,
            CreativeContextPackageSelection.MaxStepLength);

        if (omittedIngredients > 0)
        {
            omissions.Add(CreativeContextOmission.RecipeIngredientsOverCap);
        }

        if (omittedSteps > 0)
        {
            omissions.Add(CreativeContextOmission.RecipeStepsOverCap);
        }

        return new CreativeContextRecipeEntry(
            reference.Id,
            recipeId,
            pinned,
            snapshot.Value.VersionNumber,
            document.Recipe.Title,
            string.IsNullOrWhiteSpace(document.Recipe.YieldText) ? null : document.Recipe.YieldText.Trim(),
            document.Recipe.PrepTimeMinutes,
            document.Recipe.CookTimeMinutes,
            document.Recipe.RestTimeMinutes,
            document.Recipe.TotalTimeMinutes,
            ingredients,
            steps,
            omittedIngredients,
            omittedSteps,
            LatestRecipeVersionId: detail.Value.CurrentVersion?.Id);
    }

    private static CreativeContextSections SectionOf(CreativeContextReferenceKind kind) => kind switch
    {
        CreativeContextReferenceKind.Recipe => CreativeContextSections.Recipe,
        CreativeContextReferenceKind.RecipeConcept => CreativeContextSections.Concept,
        CreativeContextReferenceKind.DamAsset => CreativeContextSections.Pictures,
        CreativeContextReferenceKind.GeneratedImage => CreativeContextSections.Pictures,
        CreativeContextReferenceKind.PromptRecord => CreativeContextSections.Prompts,

        // A post package, and any kind added before it is taught here: shown to no task.
        _ => CreativeContextSections.None,
    };

    private static int CapFor(CreativeContextSections section) => section switch
    {
        CreativeContextSections.Recipe => CreativeContextPackageSelection.MaxRecipes,
        CreativeContextSections.Concept => CreativeContextPackageSelection.MaxConcepts,
        CreativeContextSections.Pictures => CreativeContextPackageSelection.MaxPictures,
        CreativeContextSections.Prompts => CreativeContextPackageSelection.MaxPrompts,
        _ => 0,
    };

    private static CreativeContextDrop Drop(CreativeContextReference reference, CreativeContextDropReason reason) =>
        new(reference.Id, reference.Kind, reason);
}
