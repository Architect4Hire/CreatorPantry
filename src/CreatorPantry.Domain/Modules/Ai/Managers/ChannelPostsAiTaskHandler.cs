using System.Globalization;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.ChannelPosts"/> (AF.6.3): writes one post for each channel the creator picked,
/// about one piece of creative work, in the brand's voice.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The model writes copy; everything else is code.</strong> Which channels exist and what each allows
/// come from <see cref="IContentChannelProfileCatalog"/>. What the piece is about comes from the creative
/// context package (AF.1.5), assembled by the content module under the workspace filter. The voice comes from
/// the approved brand package. After the answer validates, each body is measured by its channel's profile and
/// read by the claim scanners, and what they find is written beside the post by the server — a model cannot
/// remove a finding, and a body over its limit is stored as written and flagged, never trimmed.
/// </para>
/// <para>
/// <strong>Two refusals happen before any provider is paid.</strong> A piece of work with nothing to write
/// from, and one that pins a recipe version that is no longer the latest. The second because a post written
/// against a superseded recipe could never be accepted as current; the creator re-pins or regenerates.
/// </para>
/// <para>
/// <strong>It stops at the proposal.</strong> Turning the stored rows into post revisions needs the proposal's
/// id, which exists only once the worker has stored it, so that step is the request seam's (AF.6.4). What it
/// needs is all on the proposal: the rows, the brand provenance and the creative-context provenance.
/// </para>
/// <para>
/// <strong>One call for every channel, and so one brand package.</strong> The package is assembled with no
/// channel, so each post follows the guide's general guidance rather than a per-channel variant. One call per
/// channel would use the variants at that many times the cost.
/// </para>
/// </remarks>
internal sealed class ChannelPostsAiTaskHandler(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    ICreativeContextPackageFacade creativeContexts,
    IBrandContextAssembler brandContext,
    IContentChannelProfileCatalog profiles,
    IClock clock) : IAiTaskHandler
{
    /// <summary>The template's one declared input: the channels to write, and what each allows.</summary>
    public const string ChannelsInput = "channels";

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var contextId = ChannelPostsInputs.ReadContextId(context.Inputs);
        var channelKeys = ChannelPostsInputs.ReadChannelKeys(context.Inputs);

        if (contextId is null
            || channelKeys.Count is 0 or > AiPolicy.MaxChannelPostsChannels
            || channelKeys.Distinct(StringComparer.Ordinal).Count() != channelKeys.Count)
        {
            return Failure(
                AiFailureCategory.Validation,
                "This request does not say which piece of work to write for, or which channels.");
        }

        // A channel with no profile cannot be measured, so nothing is written for it. A retired channel still
        // has its profile, which is what lets a post already written for one be regenerated.
        var requested = channelKeys.Select(profiles.Find).ToList();

        if (requested.Any(profile => profile is null))
        {
            return Failure(AiFailureCategory.Validation, "One of the channels named is not one posts are written for.");
        }

        // Through the content module's facade, under the workspace filter: another workspace's context is not
        // found rather than found and rejected, and the two get one answer.
        var assembled = await creativeContexts.AssembleAsync(contextId.Value, AiTaskType.ChannelPosts, cancellationToken);

        if (!assembled.Succeeded)
        {
            return Failure(AiFailureCategory.DomainInvalid, "That piece of work is no longer available to write for.");
        }

        var package = assembled.Value!;

        if (!HasSomethingToWriteFrom(package))
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "There is nothing to write a post about yet. Give this piece of work a title, a recipe or a concept first.");
        }

        if (package.Recipes.Any(recipe =>
                recipe.LatestRecipeVersionId is { } latest && latest != recipe.RecipeVersionId))
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "This piece of work is pinned to an older version of its recipe. Point it at the current "
                    + "version before writing posts, so they are not written about a recipe that has moved on.");
        }

        var template = templates.Get(AiTaskCatalog.ChannelPosts);
        var brand = await AssembleBrandAsync(cancellationToken);
        var builder = Envelope(context, template, requested!, package, brand);

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiChannelPostsOutputDocument>(
                builder.Build(),
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiChannelPostsOutputValidator.For(channelKeys)),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var (changes, warnings) = Translate(outcome.Document!, requested!, package, profiles.Version);

        // Server-written, because only the server knows what it failed to fetch. Posts are the brand speaking,
        // so a creator who silently got none of their guidance would be reading copy they believe is in voice.
        if (brand is null)
        {
            warnings.Insert(0, new AiOutputWarning
            {
                Kind = AiWarningKind.Limitation,
                Message = $"[{ChannelPostFindings.BrandGuidanceUnavailable}] These posts were written without your "
                    + "brand guidance: none could be read. Check the voice before accepting any of them.",
                ChangeIndex = null,
            });
        }
        var attempt = outcome.Attempts[^1];
        var pinned = package.Recipes.Count > 0 ? package.Recipes[0].RecipeVersionId : (Guid?)null;

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: pinned,

            // Checked above against the recipe as it stood when the package was read, which is the same read.
            currentVersionId: pinned,
            new AiOutputDocument { SchemaVersion = template.OutputSchemaVersion, Warnings = warnings },
            changes,
            new AiProposalProvenance(
                template.OutputSchemaVersion,
                template.Id,
                template.Version.ToString(),
                template.BodyChecksum,
                attempt.ProviderName,
                attempt.ModelName,
                attempt.ModelDeployment),
            clock.UtcNow,
            brand,
            package);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, outcome.Attempts)
            : AiTaskHandlerOutcome.ForFailure(
                assembly.Failure!.Category, assembly.Failure.Message, outcome.Attempts);
    }

    /// <summary>
    /// Whether the package says anything a post could be about.
    /// </summary>
    /// <remarks>
    /// A day, a theme or an undescribed picture alone is not a subject: "it is Monday" and "a picture is
    /// attached" give a model nothing to write except invention, which is the thing this capability may not do.
    /// </remarks>
    private static bool HasSomethingToWriteFrom(CreativeContextPackage package) =>
        package.Words?.WorkingTitle is not null
        || package.Words?.PictureBrief is not null
        || package.Recipes.Count > 0
        || package.Concepts.Count > 0
        || package.Pictures.Any(picture => picture.Description is not null);

    private async Task<BrandContextPackage?> AssembleBrandAsync(CancellationToken cancellationToken)
    {
        var request = BrandContextRequestSelection.Default.ToRequest(AiTaskType.ChannelPosts);

        if (request is null)
        {
            return null;
        }

        var assembled = await brandContext.AssembleAsync(request, cancellationToken);

        return assembled.Succeeded ? assembled.Value : null;
    }

    /// <summary>The envelope, assembled in one place so every segment's trust level is visible together.</summary>
    private static PromptEnvelopeBuilder Envelope(
        AiTaskExecutionContext context,
        PromptTemplate template,
        IReadOnlyList<ContentChannelProfile> requested,
        CreativeContextPackage package,
        BrandContextPackage? brand)
    {
        // The channel list is the one thing in the task that varies, and every word of it is the server's:
        // keys from the catalogue and figures from the profiles. Nothing a creator typed is rendered into it.
        var builder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ChannelsInput] = ChannelBrief(requested),
            }))
            .WithOutputSchema(AiChannelPostsOutputSchema.Json);

        if (brand is not null)
        {
            if (BrandContextPromptRenderer.Guidance(brand) is { } guidance)
            {
                builder = builder.WithPreferences(context.WorkspaceId, guidance);
            }

            if (BrandContextPromptRenderer.Excerpts(brand) is { } excerpts)
            {
                builder = builder.AddReference(context.WorkspaceId, excerpts);
            }
        }

        // Stamped with the workspace the context was read in, which the builder refuses if it is not its own.
        return CreativeContextPromptRenderer.AddTo(builder, package);
    }

    /// <summary>
    /// One line per requested channel: what to write and what the channel allows.
    /// </summary>
    /// <remarks>
    /// Told to the model so it can write to length, and not relied on: the same profile measures the answer
    /// afterwards, and that measurement — not this sentence — is what a creator is shown.
    /// </remarks>
    internal static string ChannelBrief(IReadOnlyList<ContentChannelProfile> requested) =>
        string.Join('\n', requested.Select(profile =>
        {
            var parts = new List<string>
            {
                $"{Describe(profile.OutputKind)}, at most {Number(profile.MaxLength)} {Describe(profile.Counting)}",
                profile.MaxHashtags switch
                {
                    null => "hashtags allowed",
                    0 => "no hashtags",
                    1 => "at most 1 hashtag",
                    var most => $"at most {Number(most.Value)} hashtags",
                },
            };

            if (profile.HashtagMaxLength is { } tagLength)
            {
                parts.Add($"a hashtag of at most {Number(tagLength)} characters");
            }

            if (profile.MaxMentions is { } mentions)
            {
                parts.Add($"at most {Number(mentions)} @mentions");
            }

            if (profile.LinkPolicy is ChannelLinkPolicy.Flagged)
            {
                parts.Add("no web address in the body");
            }
            else if (profile.MaxLinks is { } links)
            {
                parts.Add($"at most {Number(links)} links");
            }

            return $"- {profile.ChannelKey}: {string.Join("; ", parts)}.";
        }));

    private static string Describe(ChannelOutputKind kind) => kind switch
    {
        ChannelOutputKind.Caption => "a caption",
        ChannelOutputKind.PinDescription => "a pin description",
        ChannelOutputKind.BlogIntro => "a blog introduction",
        ChannelOutputKind.NewsletterBlurb => "a newsletter blurb",
        _ => "a post",
    };

    private static string Describe(ChannelCountingRule rule) => rule switch
    {
        ChannelCountingRule.XWeighted => "characters as X counts them (an emoji counts as two, a link as 23)",
        ChannelCountingRule.ThreadsEmojiBytes => "characters (an emoji counts as several)",
        _ => "characters",
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The validated answer as the rows a proposal is stored as, with what the server found about each body.
    /// </summary>
    /// <remarks>
    /// One target per channel, in the order the request named them. The body is the Add row; the channel key
    /// and the profile's measurement are Set rows on the same target. Every warning about a channel points at
    /// that channel's body row.
    /// </remarks>
    internal static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiChannelPostsOutputDocument document,
        IReadOnlyList<ContentChannelProfile> requested,
        CreativeContextPackage package,
        string profileVersion)
    {
        var changes = new List<AiResolvedChange>();
        var warnings = new List<AiOutputWarning>();
        var bodyRows = new Dictionary<string, int>(StringComparer.Ordinal);
        var source = SourceFacts(package);

        for (var index = 0; index < requested.Count; index++)
        {
            var profile = requested[index];

            // Stored exactly as written. Not trimmed, not shortened, not normalised.
            var body = document.Posts.Single(post => post.ChannelKey == profile.ChannelKey).Body;
            var measured = ContentChannelWriting.Measure(profile, body, profileVersion);
            var targetId = Guid.NewGuid();
            var bodyRow = changes.Count;

            bodyRows[profile.ChannelKey] = bodyRow;

            changes.Add(new AiResolvedChange(
                AiChangeKind.Add, AiChangeTargetKind.ChannelPost, targetId, FieldName: null,
                BeforeValue: null, body, ProposedPosition: index, bodyRow));

            void Set(string field, string value) => changes.Add(new AiResolvedChange(
                AiChangeKind.Set, AiChangeTargetKind.ChannelPost, targetId, field,
                BeforeValue: null, value, ProposedPosition: null, changes.Count));

            Set(ChannelPostFields.ChannelKey, profile.ChannelKey);
            Set(ChannelPostFields.CharacterCount, Number(measured.Count));
            Set(ChannelPostFields.CharacterLimit, Number(measured.Limit));
            Set(ChannelPostFields.LimitStatus, measured.IsOverLimit ? ChannelPostFields.Over : ChannelPostFields.Within);
            Set(ChannelPostFields.ProfileVersion, measured.ProfileVersion);

            // Every server finding carries its code in brackets, as the other writing capabilities' do, so a
            // reader can tell what the server found from what the model said about its own work.
            foreach (var finding in measured.Findings)
            {
                warnings.Add(new AiOutputWarning
                {
                    Kind = AiWarningKind.Limitation,
                    Message = $"[{ChannelPostFindings.CodeFor(finding.Code)}] {Explain(profile, finding)}",
                    ChangeIndex = bodyRow,
                });
            }

            foreach (var (kind, code, message) in Claims(body, source))
            {
                warnings.Add(new AiOutputWarning { Kind = kind, Message = $"[{code}] {message}", ChangeIndex = bodyRow });
            }
        }

        // The model's own warnings last, so a server finding is never pushed out of view by one, and labelled
        // as the model's. The validator has already refused one that imitates a server finding.
        foreach (var warning in document.Warnings)
        {
            warnings.Add(new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = AiPolicy.ModelWarningLabel + warning.Message,
                ChangeIndex = warning.ChannelKey is { } key ? bodyRows[key] : null,
            });
        }

        return (changes, warnings);
    }

    /// <summary>
    /// What the claim scanners find in one body: an unsupported figure, a safety, allergen, dietary or health
    /// claim, storage advice, invented provenance, and a search metric.
    /// </summary>
    /// <remarks>
    /// Findings, not refusals — see <see cref="AiChannelPostsOutputValidator"/> for why. With no recipe in the
    /// package there is nothing for a figure to agree with, so the figure check is skipped and every check
    /// that needs no source still runs: with no recipe, storage advice and a family story are unsupported by
    /// definition.
    /// </remarks>
    private static IEnumerable<(AiWarningKind Kind, string Code, string Message)> Claims(
        string body, AiEditorialSourceFacts? source) =>
        (source is null
            ? AiEditorialClaimScanner.ScanTextWithoutSource(body)
            : AiEditorialClaimScanner.ScanText(body, source))
        .Concat(AiSeoClaimScanner.ScanTextForMetrics(body));

    /// <summary>
    /// What the package's recipes state, for a claim to be checked against; null when it carries no recipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Figures are taken from the recipe alone: its lines, yield, recorded times and title. Support for a
    /// <em>claim</em> is taken only from titles — the creator's own naming of the dish and of the piece — never
    /// from an ingredient line or a step, which is the scanner's own rule: "plain flour" in a list does not
    /// make a post saying "gluten-free" true, while a recipe titled "Gluten-Free Brownies" may be called that.
    /// </para>
    /// <para>
    /// <strong>The working title supports a claim and never a figure.</strong> It is a label on the piece of
    /// work, not a recipe fact, so "Ready in 10 Minutes" as a working title does not make ten minutes a time
    /// the recipe states. It is therefore added to the prose after the figures have been read.
    /// </para>
    /// </remarks>
    private static AiEditorialSourceFacts? SourceFacts(CreativeContextPackage package)
    {
        if (package.Recipes.Count == 0)
        {
            return null;
        }

        var figures = new List<string>();

        foreach (var recipe in package.Recipes)
        {
            figures.AddRange(recipe.Ingredients);
            figures.AddRange(recipe.Steps);

            if (recipe.YieldText is { } yield)
            {
                figures.Add(yield);
            }

            foreach (var minutes in new[] { recipe.PrepTimeMinutes, recipe.CookTimeMinutes, recipe.RestTimeMinutes, recipe.TotalTimeMinutes })
            {
                if (minutes is { } value)
                {
                    figures.Add($"{Number(value)} minutes");
                }
            }
        }

        var recipeTitles = string.Join(' ', package.Recipes.Select(recipe => recipe.Title));
        var facts = AiEditorialSourceFacts.Of(figures, recipeTitles, storageNotes: null);

        return package.Words?.WorkingTitle is { } workingTitle
            ? facts with { Prose = $"{recipeTitles} {workingTitle}" }
            : facts;
    }

    private static string Explain(ContentChannelProfile profile, ChannelWritingFinding finding) => finding.Code switch
    {
        ChannelWritingFindingCode.OverLength =>
            $"This {profile.ChannelKey} post counts {Number(finding.Actual)} against a limit of "
                + $"{Number(finding.Allowed ?? profile.MaxLength)}. It is shown as written and was not shortened.",
        ChannelWritingFindingCode.TooManyHashtags when finding.Allowed is 0 =>
            $"This {profile.ChannelKey} post has {Number(finding.Actual)} hashtag(s), and hashtags do not belong there.",
        ChannelWritingFindingCode.TooManyHashtags =>
            $"This {profile.ChannelKey} post has {Number(finding.Actual)} hashtags; at most "
                + $"{Number(finding.Allowed ?? 0)} are allowed.",
        ChannelWritingFindingCode.HashtagTooLong =>
            $"A hashtag in this {profile.ChannelKey} post is {Number(finding.Actual)} characters; at most "
                + $"{Number(finding.Allowed ?? 0)} are allowed.",
        ChannelWritingFindingCode.TooManyMentions =>
            $"This {profile.ChannelKey} post has {Number(finding.Actual)} @mentions; at most "
                + $"{Number(finding.Allowed ?? 0)} are allowed.",
        ChannelWritingFindingCode.TooManyLinks =>
            $"This {profile.ChannelKey} post has {Number(finding.Actual)} links; at most "
                + $"{Number(finding.Allowed ?? 0)} are allowed.",
        ChannelWritingFindingCode.LinkDiscouraged =>
            $"This {profile.ChannelKey} post has a web address in its body. Check it is one of yours: none was "
                + "supplied to write from, and on this channel a link usually belongs elsewhere.",
        ChannelWritingFindingCode.MarkupFound =>
            $"This {profile.ChannelKey} post contains link markup, which will be shown as typed rather than as a link.",
        _ => $"This {profile.ChannelKey} post does not meet its channel's writing profile.",
    };

    private static AiTaskHandlerOutcome Failure(AiFailureCategory category, string message) =>
        AiTaskHandlerOutcome.ForFailure(category, message, []);
}

/// <summary>The codes AF.6.3's own server findings are stored under, in brackets at the head of the message.</summary>
/// <remarks>
/// Claim findings keep the code of the scanner that raised them (<c>editorial.*</c>, <c>seo.*</c>). These are
/// the ones only this capability raises: what a channel's writing profile measured, and guidance the server
/// could not fetch.
/// </remarks>
public static class ChannelPostFindings
{
    public const string OverLimit = "posts.over_limit";

    public const string Hashtags = "posts.hashtags";

    public const string Mentions = "posts.mentions";

    public const string Links = "posts.links";

    public const string Markup = "posts.markup";

    public const string BrandGuidanceUnavailable = "posts.brand_guidance_unavailable";

    public static string CodeFor(CreatorPantry.Domain.Managers.Reference.ChannelWritingFindingCode code) => code switch
    {
        CreatorPantry.Domain.Managers.Reference.ChannelWritingFindingCode.OverLength => OverLimit,
        CreatorPantry.Domain.Managers.Reference.ChannelWritingFindingCode.TooManyHashtags
            or CreatorPantry.Domain.Managers.Reference.ChannelWritingFindingCode.HashtagTooLong => Hashtags,
        CreatorPantry.Domain.Managers.Reference.ChannelWritingFindingCode.TooManyMentions => Mentions,
        CreatorPantry.Domain.Managers.Reference.ChannelWritingFindingCode.MarkupFound => Markup,
        _ => Links,
    };
}
