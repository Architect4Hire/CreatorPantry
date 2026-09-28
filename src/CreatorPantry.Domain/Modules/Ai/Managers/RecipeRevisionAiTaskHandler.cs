using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.RecipeRevision"/> (AIREC-003): revises one pinned version of an existing recipe
/// within the section the creator selected, towards the goal they stated.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The first task that revises rather than originates</strong>, so unlike AIREC-001 and AIREC-002 it
/// works the recipe-bound path end to end: it reads the pinned snapshot through the recipe module's facade,
/// and every change the model returns is resolved against that snapshot by
/// <see cref="AiDiffCalculator"/> — which is where a before value comes from, and why the model has nowhere
/// to supply one.
/// </para>
/// <para>
/// <strong>The scope is told to the model and enforced regardless.</strong> The template renders the section,
/// the settable fields and the applicable change kinds into the instructions so the model knows its bound;
/// <see cref="AiOutputValidator"/> then refuses anything outside it, non-correctably. The first is a courtesy
/// that makes a good answer likelier. Only the second is a guarantee, and it does not depend on the model
/// having read the first.
/// </para>
/// <para>
/// <strong>The creator's goal is untrusted text and travels as one.</strong> It goes in a
/// <see cref="PromptSegmentKind.Preferences"/> segment at <see cref="PromptSegmentTrust.CreatorData"/>, like
/// AIREC-001's brief — a goal is a sentence a creator typed, and ai.md asks that user instructions be treated
/// as untrusted prompt content. The recipe travels as <see cref="PromptSegmentKind.Source"/> for the same
/// reason: a headnote can carry an injection as easily as an import can.
/// </para>
/// </remarks>
internal sealed class RecipeRevisionAiTaskHandler(
    IAiCompletionGateway gateway,
    IRecipeFacade recipes,
    IPromptTemplateStore templates,
    IClock clock) : IAiTaskHandler
{
    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.RecipeId is not { } recipeId || context.RecipeVersionId is not { } versionId)
        {
            // Unreachable through the request seam, which requires both. Refused rather than assumed, because
            // a revision with no pinned source has nothing to compute a before value against.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "A revision names a recipe and the exact version it was asked against.",
                []);
        }

        var snapshot = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);

        if (!snapshot.Succeeded)
        {
            // The recipe or the version is gone, or belongs to another workspace and is therefore invisible.
            // Its own category: retrying will not bring it back.
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation,
                "The recipe version this revision was asked against is no longer available.",
                []);
        }

        var template = templates.Get(AiTaskCatalog.RecipeRevision);
        var source = snapshot.Value!.Document;

        var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
            .WithTask(template.Render(Bounds(context.Scope)))
            .WithOutputSchema(AiOutputSchema.Json)
            .WithSource(context.WorkspaceId, JsonSerializer.Serialize(source))
            .WithPreferences(context.WorkspaceId, RenderGoal(context.Inputs))
            .Build();

        var outcome = await gateway.CompleteAsync(
            new AiCompletionRequest<AiOutputDocument>(
                envelope,
                template.OutputSchemaVersion,
                context.Scope,
                template.Id,
                template.Version.ToString(),
                context.CorrelationId,
                AiOutputValidator.AsDelegate),
            cancellationToken);

        if (!outcome.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        }

        // Every before value comes from here, computed against the pinned snapshot. A model that wanted to
        // assert one has nowhere to put it, and this would ignore it if it did.
        var diff = AiDiffCalculator.Calculate(source, outcome.Document!);

        if (!diff.Succeeded)
        {
            return AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.DomainInvalid, diff.Failure!.Message, outcome.Attempts);
        }

        var attempt = outcome.Attempts[^1];

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: versionId,
            currentVersionId: versionId,
            outcome.Document!,
            diff.Changes,
            new AiProposalProvenance(
                template.OutputSchemaVersion,
                template.Id,
                template.Version.ToString(),
                template.BodyChecksum,
                attempt.ProviderName,
                attempt.ModelName,
                attempt.ModelDeployment),
            clock.UtcNow);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, outcome.Attempts)
            : AiTaskHandlerOutcome.ForFailure(
                assembly.Failure!.Category, assembly.Failure.Message, outcome.Attempts);
    }

    /// <summary>
    /// The bound, rendered for the model from the same tables that enforce it.
    /// </summary>
    /// <remarks>
    /// Derived rather than written out in the template, so the instructions cannot describe a bound the
    /// validator does not apply. A creator who scoped a revision to one section is told about that section and
    /// no other — the model is never shown the wider vocabulary and asked to restrain itself.
    /// </remarks>
    private static Dictionary<string, string> Bounds(AiOperationScope scope)
    {
        var targets = AiPolicy.AllowedTargets(scope)
            .Where(target => AiChangeApplicability.For(target).Count > 0)
            .OrderBy(target => target.ToString(), StringComparer.Ordinal)
            .ToList();

        var fields = targets
            .SelectMany(target => AiPolicy.AllowedFields(scope, target).Select(field => $"{target}.{field}"))
            .OrderBy(field => field, StringComparer.Ordinal)
            .ToList();

        var kinds = targets
            .SelectMany(target => AiChangeApplicability.For(target).Select(kind => $"{kind} on {target}"))
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToList();

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scope"] = Describe(scope),

            // "none" rather than an empty line: a bound that reads as a blank invites a model to treat it as
            // absent, which is the opposite of what an empty allow-list means.
            ["allowedFields"] = fields.Count == 0 ? "none" : string.Join(", ", fields),
            ["allowedChanges"] = kinds.Count == 0 ? "none" : string.Join(", ", kinds),
        };
    }

    /// <summary>The scope in the creator's terms rather than the enum's.</summary>
    private static string Describe(AiOperationScope scope) => scope switch
    {
        AiOperationScope.WholeRecipe => "the whole recipe",
        AiOperationScope.Ingredients => "the ingredient list",
        AiOperationScope.Instructions => "the method",
        AiOperationScope.Metadata => "the recipe's framing — its title, description, headnote, times and yield",
        AiOperationScope.Media => "the recipe's media",
        _ => "nothing",
    };

    /// <summary>
    /// The creator's goal, defaulting to a general improvement when they stated none.
    /// </summary>
    /// <remarks>
    /// A revision with no goal is a legitimate ask — "look at this section and tell me what you would change"
    /// — so an absent goal reads as a complete sentence rather than a blank the model has to interpret.
    /// </remarks>
    private static string RenderGoal(IReadOnlyDictionary<string, string>? supplied)
    {
        var goal = supplied is not null
            && supplied.TryGetValue(AiRevisionInputs.Goal, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : "not specified — suggest what you would improve in this section";

        return $"""
            Goal for this revision: {goal}
            """;
    }
}

/// <summary>The keys AIREC-003's request writes into <c>AiOperation.TaskInputsJson</c>.</summary>
/// <remarks>
/// The same agreement <see cref="AiFirstDraftInputs"/> records, for the same reason: the request seam writes
/// this and the handler reads it back in another process, with no type between them.
/// </remarks>
public static class AiRevisionInputs
{
    /// <summary>What the creator asked the revision to achieve, in their own words.</summary>
    public const string Goal = "goal";
}
