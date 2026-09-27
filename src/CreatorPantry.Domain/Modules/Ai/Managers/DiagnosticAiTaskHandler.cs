using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Runs <see cref="AiTaskType.Diagnostic"/>: exercises the operation lifecycle end to end — including loading
/// the pinned recipe version, when the operation names one, and producing a stored proposal — without calling
/// a model.
/// </summary>
/// <remarks>
/// <para>
/// The one production handler this worker ships with. Real generation capabilities arrive with their own
/// handler and their own prompt template (<c>add-ai-capability</c>); this one exists so the queue, the
/// claim/lease machinery, and the diff/assemble/store path can all be proven against a live worker — including
/// in a deployment with no model configured — without spending a provider budget. <see cref="AiTaskType.Diagnostic"/>
/// says so directly: "without calling a model".
/// </para>
/// <para>
/// Always answers with an empty proposal: "nothing needs changing" is a real, valid answer
/// (<see cref="AiOutputDocument"/>'s own remarks), and it is the only honest one from a handler that never
/// looked at the recipe's content.
/// </para>
/// </remarks>
internal sealed class DiagnosticAiTaskHandler(IRecipeFacade recipes, IClock clock) : IAiTaskHandler
{
    private const string ProviderName = "diagnostic";

    private const string ModelName = "none";

    private const string SchemaVersion = "diagnostic.v1";

    public async Task<AiTaskHandlerOutcome> HandleAsync(
        AiTaskExecutionContext context, CancellationToken cancellationToken)
    {
        Guid? pinnedVersionId = null;
        Guid? currentVersionId = null;

        if (context.RecipeId is { } recipeId)
        {
            var detail = await recipes.GetDetailAsync(recipeId, cancellationToken);

            if (!detail.Succeeded)
            {
                return AiTaskHandlerOutcome.ForFailure(
                    AiFailureCategory.DomainInvalid, "The recipe this operation named is no longer visible.", []);
            }

            currentVersionId = detail.Value!.CurrentVersion?.Id;

            if (context.RecipeVersionId is { } recipeVersionId)
            {
                pinnedVersionId = recipeVersionId;

                // Proves the pinned-snapshot read this task exists to exercise, even though an empty answer's
                // diff never reads the document's content.
                var snapshot = await recipes.GetSnapshotAsync(recipeId, recipeVersionId, cancellationToken);

                if (!snapshot.Succeeded)
                {
                    return AiTaskHandlerOutcome.ForFailure(
                        AiFailureCategory.DomainInvalid,
                        "The recipe version this operation pinned is no longer available.",
                        []);
                }
            }
        }

        // No call to AiDiffCalculator.Calculate: with zero changes, every resolution branch it has starts from
        // output.Changes, so it would do nothing but demand a source document this handler has no use for
        // building. An empty diff is the diff of an empty answer, not a shortcut around computing one.
        var output = new AiOutputDocument { SchemaVersion = SchemaVersion };

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId,
            currentVersionId,
            output,
            [],
            new AiProposalProvenance(SchemaVersion, AiTaskCatalog.Diagnostic, "1.0.0", "none", ProviderName, ModelName, null),
            clock.UtcNow);

        return assembly.Succeeded
            ? AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, [])
            : AiTaskHandlerOutcome.ForFailure(assembly.Failure!.Category, assembly.Failure.Message, []);
    }
}
