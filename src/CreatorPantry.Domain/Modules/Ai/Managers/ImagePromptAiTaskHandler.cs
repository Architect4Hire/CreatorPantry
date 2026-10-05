using System.Text.Encodings.Web;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.ImagePrompt"/> (IMG-002): composes one editable image prompt for one shot of a
/// concept the creator chose, from that concept plus the channel's visual guidance, the recipe when there is
/// one, their own overrides, and a brief they uploaded.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its source is another operation's proposal, which no other handler's is.</strong> AIREC-008 reads a
/// proposal too, but deterministically and to explain it; this reads one to compose from. The concept is
/// reconstructed from the <c>AiStructuredChange</c> rows IMG-001 stored, by the field names
/// <see cref="PhotographyConceptFields"/> defines — so the pair cannot drift without a test failing.
/// </para>
/// <para>
/// <strong>The concept is the approval.</strong> IMG-001 ships no acceptance seam — a concept has no stored
/// "approved" state to check — so a creator choosing a concept and a shot and asking for its prompt is the act
/// of approving it. What this handler enforces instead is that the concept is one they were actually shown:
/// the operation must be this workspace's, must have run <see cref="AiTaskType.PhotographyConcept"/>, must
/// have produced a proposal, and must hold that concept id with that shot planned.
/// </para>
/// <para>
/// <strong>It renders nothing and composes no rendering setting.</strong>
/// <see cref="AiImagePromptOutputDocument"/> has no field for a provider, model, seed or dimension, and
/// <see cref="AiImagePromptOutputValidator"/> refuses one written into the prose instead — because the
/// creator saves this text, and a setting in it is something they would have to edit out.
/// </para>
/// <para>
/// <strong>The brief is the most dangerous input this module takes, and it is treated that way.</strong> A
/// brief is written to instruct somebody, so an instruction inside one is the norm rather than an anomaly. It
/// is read through the brand module's facades under the workspace filter, capped, and carried at
/// <c>UntrustedText</c> trust beside the creator's own overrides — never folded into the task instructions
/// (ai.md, and this capability's own RESTRICTION).
/// </para>
/// </remarks>
internal sealed class ImagePromptAiTaskHandler(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    IAiOperationDataLayer operations,
    IBrandContextAssembler brandContext,
    IRecipeFacade recipes,
    IBrandSourcePassageFacade passages,
    IClock clock) : IAiTaskHandler
{
    private static readonly Dictionary<string, string> NoTemplateInputs = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions SegmentJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var conceptRequestId = ImagePromptInputs.ReadId(context.Inputs, ImagePromptInputs.ConceptRequestId);
        var conceptId = ImagePromptInputs.ReadId(context.Inputs, ImagePromptInputs.ConceptId);
        var shotKind = ImagePromptInputs.ReadShotKind(context.Inputs);

        if (conceptRequestId is null || conceptId is null || shotKind is null)
        {
            return Failure(
                AiFailureCategory.Validation,
                "This request does not say which concept and shot to compose for.");
        }

        var concept = await ConceptAsync(
            conceptRequestId.Value, conceptId.Value, shotKind.Value, cancellationToken);

        if (concept is null)
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "That concept and shot are no longer available to compose from.");
        }

        var source = await SourceAsync(context, cancellationToken);

        if (source.Failed)
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "The recipe this prompt was composed for is no longer available.");
        }

        var template = templates.Get(AiTaskCatalog.ImagePrompt);
        var channelKey = ImagePromptInputs.Read(context.Inputs, ImagePromptInputs.ChannelKey);
        var package = await AssembleAsync(channelKey, cancellationToken);
        var brief = await BriefAsync(context.Inputs, cancellationToken);
        var builder = Envelope(context, template, concept, source.Recipe, package, brief.Text);

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiImagePromptOutputDocument>(
                builder.Build(),
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiImagePromptOutputValidator.AsDelegate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var (changes, warnings) = Translate(outcome.Document!);

        // Server-written, because only the server knows what it failed to fetch. A creator who named a
        // channel or attached a brief and silently got neither would be reading a prompt they believe is
        // grounded in both.
        if (channelKey is not null && package is null)
        {
            warnings.Add(Limitation(
                "This prompt was composed without your brand's visual guidance: none could be read for that "
                    + "channel. Check it against your own look before using it."));
        }

        if (brief.Requested && brief.Text is null)
        {
            warnings.Add(Limitation(
                "This prompt was composed without the brief you attached: no text could be read from it yet."));
        }
        else if (brief.Truncated)
        {
            warnings.Add(Limitation(
                "Only the beginning of the brief you attached was used: it is longer than one request can "
                    + "carry. Check that what matters most is near the start of it."));
        }

        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: context.RecipeVersionId,
            currentVersionId: source.CurrentVersionId,
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
            package);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, outcome.Attempts)
            : AiTaskHandlerOutcome.ForFailure(
                assembly.Failure!.Category, assembly.Failure.Message, outcome.Attempts);
    }

    /// <summary>
    /// The concept and shot this request names, or null when it names a pair the creator was not shown.
    /// </summary>
    /// <remarks>
    /// Read through this module's own data layer, which is scoped by the workspace query filter — so another
    /// workspace's concept request is not found rather than found and rejected. The task-type check is what
    /// stops any proposal in the workspace standing in for a photography concept.
    /// </remarks>
    private async Task<AiImagePromptConcept?> ConceptAsync(
        Guid conceptRequestId,
        Guid conceptId,
        AiPhotographyShotKind shotKind,
        CancellationToken cancellationToken)
    {
        var found = await operations.GetWithProposalAsync(conceptRequestId, cancellationToken);

        if (found?.Proposal is null || found.Operation.TaskType is not AiTaskType.PhotographyConcept)
        {
            return null;
        }

        return AiImagePromptConcept.From(
            found.Proposal.Changes.Select(change =>
                (change.TargetKind, change.TargetId, change.FieldName, change.AfterValue)),
            conceptId,
            shotKind);
    }

    /// <summary>
    /// The pinned recipe for the source segment, plus which version is current — or nothing, which is complete.
    /// </summary>
    private async Task<(bool Failed, object? Recipe, Guid? CurrentVersionId)> SourceAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.RecipeId is not { } recipeId)
        {
            return (false, null, null);
        }

        if (context.RecipeVersionId is { } versionId)
        {
            var pinned = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);

            return pinned.Succeeded ? (false, pinned.Value, versionId) : (true, null, null);
        }

        var detail = await recipes.GetDetailAsync(recipeId, cancellationToken);

        return detail.Succeeded ? (false, detail.Value, null) : (true, null, null);
    }

    /// <inheritdoc cref="PhotographyConceptAiTaskHandler"/>
    private async Task<BrandContextPackage?> AssembleAsync(
        string? channelKey, CancellationToken cancellationToken)
    {
        var request = BrandContextRequestSelection.Default.ToRequest(AiTaskType.ImagePrompt, channelKey);

        if (request is null)
        {
            return null;
        }

        var assembled = await brandContext.AssembleAsync(request, cancellationToken);

        return assembled.Succeeded ? assembled.Value : null;
    }

    /// <summary>
    /// The attached brief's text, capped — and whether one was asked for at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One facade call, for the exact document version the request pinned. The version was resolved when the
    /// request was made, so the brief that reaches the model is the one the creator attached rather than
    /// whichever version is current when the worker claims the operation — the same discipline every other
    /// version pin here follows. A document this workspace cannot read simply supplies nothing, which is the
    /// brand module's own documented behaviour and exactly the answer wanted: a neighbour's brief is
    /// indistinguishable from one whose text has not been extracted yet.
    /// </para>
    /// <para>
    /// <strong>Capped, and the creator is told which kind of capping happened.</strong> What is cut is cut at a
    /// passage boundary where it can be, so the brief that reaches the model is whole sentences rather than a
    /// sentence ending mid-word — except for a single passage longer than the whole cap, which is cut to it.
    /// Either way the creator gets a warning saying which happened, because "unreadable" and "too long" send
    /// them to look for very different problems.
    /// </para>
    /// </remarks>
    private async Task<(bool Requested, string? Text, bool Truncated)> BriefAsync(
        IReadOnlyDictionary<string, string>? inputs, CancellationToken cancellationToken)
    {
        if (ImagePromptInputs.ReadId(inputs, ImagePromptInputs.BriefDocumentId) is not { } documentId
            || ImagePromptInputs.ReadNumber(inputs, ImagePromptInputs.BriefVersionNumber) is not { } version)
        {
            return (false, null, false);
        }

        var set = await passages.ListPassagesAsync(
            [new BrandSourcePassageSelector(documentId, version)],
            cancellationToken);

        var ordered = set.Passages.OrderBy(passage => passage.Ordinal).ToList();
        var kept = new List<string>();
        var length = 0;

        foreach (var passage in ordered)
        {
            if (length + passage.Text.Length > AiPolicy.ImagePromptBriefMaxLength)
            {
                break;
            }

            kept.Add(passage.Text);
            length += passage.Text.Length;
        }

        // A brief whose very first passage is longer than the cap is cut to the cap rather than dropped. The
        // first pass broke out of the loop and reported "no text could be read", which was simply false: the
        // text was there and was too long, and telling a creator their brief is unreadable when it is merely
        // long sends them to look for a problem that does not exist.
        if (kept.Count == 0 && ordered.Count > 0)
        {
            return (true, ordered[0].Text[..AiPolicy.ImagePromptBriefMaxLength], Truncated: true);
        }

        return (true, kept.Count == 0 ? null : string.Join("\n\n", kept), Truncated: kept.Count < ordered.Count);
    }

    /// <summary>The envelope, assembled in one place so every segment's trust level is visible together.</summary>
    private static PromptEnvelopeBuilder Envelope(
        AiTaskExecutionContext context,
        PromptTemplate template,
        AiImagePromptConcept concept,
        object? recipe,
        BrandContextPackage? package,
        string? brief)
    {
        var builder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(NoTemplateInputs))
            .WithOutputSchema(AiImagePromptOutputSchema.Json)

            // The concept and the recipe share one source segment: both are this workspace's own stored
            // material, and PromptEnvelopeBuilder permits exactly one source — only References accumulate.
            .WithSource(context.WorkspaceId, JsonSerializer.Serialize(new { concept, recipe }, SegmentJson));

        if (package is not null)
        {
            if (BrandContextPromptRenderer.Guidance(package) is { } guidance)
            {
                builder = builder.WithPreferences(context.WorkspaceId, guidance);
            }

            if (BrandContextPromptRenderer.Excerpts(package) is { } excerpts)
            {
                builder = builder.AddReference(context.WorkspaceId, excerpts);
            }
        }

        if (Untrusted(context.Inputs, brief) is { } untrusted)
        {
            builder = builder.WithUntrustedText(context.WorkspaceId, untrusted);
        }

        return builder;
    }

    /// <summary>
    /// What the creator typed and what their brief says, as one untrusted segment — or null when neither.
    /// </summary>
    /// <remarks>
    /// Together rather than separately because the envelope permits one untrusted segment, and because they
    /// are the same kind of thing: material the creator supplied for this request, none of which is an
    /// instruction to the model however much of it reads like one.
    /// </remarks>
    private static string? Untrusted(IReadOnlyDictionary<string, string>? inputs, string? brief)
    {
        var scene = ImagePromptInputs.ReadList(inputs, ImagePromptInputs.SceneOverrides);
        var style = ImagePromptInputs.ReadList(inputs, ImagePromptInputs.StyleOverrides);

        if (brief is null && scene.Count == 0 && style.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            new { sceneOverrides = scene, styleOverrides = style, brief }, SegmentJson);
    }

    /// <summary>
    /// The validated prompt as the rows a proposal is stored as: one <see cref="AiChangeKind.Add"/> row
    /// carrying the prompt, and a <see cref="AiChangeKind.Set"/> row for the negative guidance.
    /// </summary>
    /// <remarks>
    /// One target, because one request composes one prompt. The avoid list is joined into a single row rather
    /// than one row each: it is read and edited as a set, and a creator who removes one entry has changed the
    /// list rather than rejected a change.
    /// </remarks>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiImagePromptOutputDocument document)
    {
        var targetId = Guid.NewGuid();

        var changes = new List<AiResolvedChange>
        {
            new(
                AiChangeKind.Add,
                AiChangeTargetKind.ImagePrompt,
                targetId,
                FieldName: null,
                BeforeValue: null,
                document.Prompt.Trim(),
                ProposedPosition: 0,
                0),
        };

        if (document.Avoid.Count > 0)
        {
            changes.Add(new AiResolvedChange(
                AiChangeKind.Set,
                AiChangeTargetKind.ImagePrompt,
                targetId,
                ImagePromptFields.Avoid,
                BeforeValue: null,
                string.Join("; ", document.Avoid),
                ProposedPosition: null,
                1));
        }

        var warnings = document.Warnings
            .Select(warning => new AiOutputWarning
            {
                Kind = warning.Kind,

                // Every warning is about the one prompt, so each points at its row rather than at nothing.
                Message = warning.Message,
                ChangeIndex = 0,
            })
            .ToList();

        return (changes, warnings);
    }

    private static AiOutputWarning Limitation(string message) =>
        new() { Kind = AiWarningKind.Limitation, Message = message, ChangeIndex = 0 };

    private static AiTaskHandlerOutcome Failure(AiFailureCategory category, string message) =>
        AiTaskHandlerOutcome.ForFailure(category, message, []);
}

/// <summary>The field names an IMG-002 prompt's rows are stored under.</summary>
/// <remarks>
/// Stable, because a stored proposal's rows are read back by whatever reads a proposal — and by the Image
/// Studio screen that lets a creator edit the prompt before saving it (12.10c).
/// </remarks>
public static class ImagePromptFields
{
    public const string Avoid = "avoid";
}
