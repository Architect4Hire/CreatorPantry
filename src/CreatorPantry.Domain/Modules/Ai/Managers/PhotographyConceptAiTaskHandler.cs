using System.Text.Encodings.Web;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.PhotographyConcept"/> (IMG-001): plans one to three ways of photographing a
/// subject, each as a short shot list, from the workspace's visual guidance for a channel plus whatever the
/// creator described.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Its recipe is optional, which no earlier handler's is.</strong> With a recipe pinned, the snapshot
/// travels as the SOURCE segment so the styling is planned around the dish the creator actually wrote; without
/// one, there is no source at all and the creator's own concept is the whole brief. Both are complete requests
/// — a creator plans shoots for recipes they have not written yet — so a missing recipe is never a failure
/// here, where for <c>EditorialPackageAiTaskHandler</c> it would be.
/// </para>
/// <para>
/// <strong>"Channel memory" is the brand context package for the channel.</strong> 11A.21 wired this task into
/// <see cref="BrandContextSelection"/>'s visual sections, so the guidance assembled here is the creator's own
/// photography direction — composition, palette, texture, lighting, props, food styling and negative guidance
/// — for the channel they named. No channel means their visual identity in general. An empty package is not a
/// failure: a workspace with no visual guide still gets concepts, planned plainly, and the prompt is explicit
/// that a missing guide is not licence to invent a house style.
/// </para>
/// <para>
/// <strong>It composes no prompt and renders no image.</strong> <see cref="AiPhotographyConceptOutputDocument"/>
/// has no field for a finished prompt, so there is nowhere for one to be stored even if a model wrote it;
/// IMG-002 composes from a concept the creator has approved. Nothing here touches a recipe either:
/// <see cref="AiChangeTargetKind.PhotographyConcept"/> is absent from <see cref="AiChangeApplicability"/>, so
/// no stored row can become an edit.
/// </para>
/// <para>
/// <strong>Everything the creator typed is untrusted.</strong> The concept and the scene and style overrides
/// travel in an untrusted segment, never folded into the task instructions, and the recipe snapshot travels as
/// creator data — a headnote can carry an injected instruction as easily as an import can (ai.md).
/// </para>
/// </remarks>
internal sealed class PhotographyConceptAiTaskHandler(
    IAiCompletionGateway gateway,
    IPromptTemplateStore templates,
    IBrandContextAssembler brandContext,
    IRecipeFacade recipes,
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

        // A version without its recipe is unresolvable; the request contract refuses it, and this is the
        // backstop for any caller that reached the worker another way.
        if (context.RecipeId is null && context.RecipeVersionId is not null)
        {
            return Failure(
                AiFailureCategory.Validation,
                "A photography concept that names a recipe version must name its recipe.");
        }

        var source = await SourceAsync(context, cancellationToken);

        if (source.Failed)
        {
            return Failure(
                AiFailureCategory.DomainInvalid,
                "The recipe this shoot was planned for is no longer available.");
        }

        var template = templates.Get(AiTaskCatalog.PhotographyConcept);
        var channelKey = PhotographyConceptInputs.Read(context.Inputs, PhotographyConceptInputs.ChannelKey);
        var package = await AssembleAsync(channelKey, cancellationToken);

        var builder = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(NoTemplateInputs))
            .WithOutputSchema(AiPhotographyConceptOutputSchema.Json);

        if (source.Json is { } recipeJson)
        {
            builder = builder.WithSource(context.WorkspaceId, recipeJson);
        }

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

        if (Brief(context.Inputs) is { } brief)
        {
            builder = builder.WithUntrustedText(context.WorkspaceId, brief);
        }

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiPhotographyConceptOutputDocument>(
                builder.Build(),
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiPhotographyConceptOutputValidator.AsDelegate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        var (changes, warnings) = Translate(outcome.Document!);

        // The creator asked for a channel's guidance and there was none to assemble, so the concepts were
        // planned plainly. That is a legitimate result, but it is not what they asked for, and a shoot plan
        // that quietly ignored their visual guide would be the silent degradation `ai.md` asks us to surface.
        // A server-written warning, because the model cannot know what the server failed to fetch.
        if (channelKey is not null && package is null)
        {
            warnings.Add(new AiOutputWarning
            {
                Kind = AiWarningKind.Limitation,
                Message = "These concepts were planned without your brand's visual guidance: none could be "
                    + "read for that channel. Review them against your own look before shooting.",
            });
        }

        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,

            // The pinned version when there is one, so a concept planned against a version the recipe has
            // since moved past is reported as stale rather than silently offered for a dish that changed.
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
    /// The pinned recipe as the SOURCE segment, plus which version is current — or nothing at all, which is a
    /// complete request rather than a failure.
    /// </summary>
    private async Task<(bool Failed, string? Json, Guid? CurrentVersionId)> SourceAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.RecipeId is not { } recipeId)
        {
            return (false, null, null);
        }

        // Through the recipe module's facade, scoped to the resolved workspace, so a neighbour's recipe is
        // indistinguishable from one that does not exist and neither reaches a prompt.
        if (context.RecipeVersionId is { } versionId)
        {
            var pinned = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);

            return pinned.Succeeded
                ? (false, JsonSerializer.Serialize(pinned.Value, SegmentJson), versionId)
                : (true, null, null);
        }

        var detail = await recipes.GetDetailAsync(recipeId, cancellationToken);

        return detail.Succeeded
            ? (false, JsonSerializer.Serialize(detail.Value, SegmentJson), null)
            : (true, null, null);
    }

    /// <summary>
    /// The channel's visual guidance, or null when the workspace has none to give.
    /// </summary>
    /// <remarks>
    /// A failure to assemble is <em>not</em> propagated. Unlike a style test drive, which exists to show one
    /// named guide version and is meaningless without it, a shoot plan is useful with no guidance at all — so
    /// an unresolvable package means concepts planned plainly, and the prompt is explicit that no guidance is
    /// not licence to invent a house style.
    /// </remarks>
    private async Task<BrandContextPackage?> AssembleAsync(string? channelKey, CancellationToken cancellationToken)
    {
        var request = BrandContextRequestSelection.Default.ToRequest(
            AiTaskType.PhotographyConcept, channelKey);

        if (request is null)
        {
            return null;
        }

        var assembled = await brandContext.AssembleAsync(request, cancellationToken);

        return assembled.Succeeded ? assembled.Value : null;
    }

    /// <summary>What the creator typed, as one untrusted segment — or null when they typed nothing.</summary>
    private static string? Brief(IReadOnlyDictionary<string, string>? inputs)
    {
        var concept = PhotographyConceptInputs.Read(inputs, PhotographyConceptInputs.CreatorConcept);
        var scene = PhotographyConceptInputs.ReadList(inputs, PhotographyConceptInputs.SceneOverrides);
        var style = PhotographyConceptInputs.ReadList(inputs, PhotographyConceptInputs.StyleOverrides);

        if (concept is null && scene.Count == 0 && style.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            new { concept, sceneOverrides = scene, styleOverrides = style }, SegmentJson);
    }

    /// <summary>
    /// The validated document as the rows a proposal is stored as: one <see cref="AiChangeKind.Add"/> row per
    /// concept carrying its label, then <see cref="AiChangeKind.Set"/> rows for the rest of the look and for
    /// every shot.
    /// </summary>
    /// <remarks>
    /// Each shot's rows are field-named <c>shot.{Kind}.{property}</c> under the concept's own id, so IMG-002
    /// can compose a prompt for one named shot of one approved concept without a shot needing a target kind of
    /// its own — a shot is not separately approvable, and a target kind would imply that it was.
    /// </remarks>
    private static (List<AiResolvedChange> Changes, List<AiOutputWarning> Warnings) Translate(
        AiPhotographyConceptOutputDocument document)
    {
        var changes = new List<AiResolvedChange>();
        var addRowIndexByConcept = new int[document.Concepts.Count];

        for (var conceptIndex = 0; conceptIndex < document.Concepts.Count; conceptIndex++)
        {
            var concept = document.Concepts[conceptIndex];
            var targetId = Guid.NewGuid();

            addRowIndexByConcept[conceptIndex] = changes.Count;
            changes.Add(new AiResolvedChange(
                AiChangeKind.Add,
                AiChangeTargetKind.PhotographyConcept,
                targetId,
                FieldName: null,
                BeforeValue: null,
                concept.Label,
                ProposedPosition: conceptIndex,
                changes.Count));

            Set(changes, targetId, PhotographyConceptFields.Mood, concept.Mood);
            Set(changes, targetId, PhotographyConceptFields.Palette, concept.Palette);
            Set(changes, targetId, PhotographyConceptFields.Rationale, concept.Rationale);
            Set(changes, targetId, PhotographyConceptFields.ChannelFit, concept.ChannelFit);

            foreach (var shot in concept.Shots)
            {
                Set(changes, targetId, PhotographyConceptFields.Shot(shot.Kind, "framing"), shot.Framing);
                Set(changes, targetId, PhotographyConceptFields.Shot(shot.Kind, "lighting"), shot.Lighting);
                Set(changes, targetId, PhotographyConceptFields.Shot(shot.Kind, "surface"), shot.Surface);
                Set(changes, targetId, PhotographyConceptFields.Shot(shot.Kind, "styling"), shot.Styling);
                Set(
                    changes,
                    targetId,
                    PhotographyConceptFields.Shot(shot.Kind, "props"),
                    shot.Props.Count == 0 ? null : string.Join("; ", shot.Props));
            }
        }

        var warnings = document.Warnings
            .Select(warning => new AiOutputWarning
            {
                Kind = warning.Kind,
                Message = warning.Message,
                ChangeIndex = warning.ConceptIndex is { } conceptIndex
                    ? addRowIndexByConcept[conceptIndex]
                    : null,
            })
            .ToList();

        return (changes, warnings);
    }

    private static void Set(List<AiResolvedChange> changes, Guid targetId, string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        changes.Add(new AiResolvedChange(
            AiChangeKind.Set,
            AiChangeTargetKind.PhotographyConcept,
            targetId,
            field,
            BeforeValue: null,
            value,
            ProposedPosition: null,
            changes.Count));
    }

    private static AiTaskHandlerOutcome Failure(AiFailureCategory category, string message) =>
        AiTaskHandlerOutcome.ForFailure(category, message, []);
}

/// <summary>
/// The field names an IMG-001 concept's rows are stored under.
/// </summary>
/// <remarks>
/// Stable, because a stored proposal's rows are read back by whatever reads a proposal — and, from 12.4a, by
/// the composer that turns one approved shot into a prompt. Renaming one orphans every concept already stored.
/// </remarks>
public static class PhotographyConceptFields
{
    public const string Mood = "mood";

    public const string Palette = "palette";

    public const string Rationale = "rationale";

    public const string ChannelFit = "channelFit";

    /// <summary>One shot's property, named by the shot's role so a concept's rows stay self-describing.</summary>
    public static string Shot(AiPhotographyShotKind kind, string property) => $"shot.{kind}.{property}";
}
