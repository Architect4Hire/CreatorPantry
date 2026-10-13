using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Limits and the switchable vocabulary for a workspace's AI features.</summary>
public static class AiFeaturePolicy
{
    /// <summary>Wide enough for any discriminator in <see cref="AiTaskCatalog"/>, with room to add longer ones.</summary>
    public const int TaskMaxLength = 64;

    /// <summary>
    /// The tasks a workspace Owner may switch, in the order a read reports them.
    /// </summary>
    /// <remarks>
    /// Every creator-facing task. <see cref="AiTaskCatalog.Diagnostic"/> is left out on purpose: it exercises
    /// the lifecycle for an operator and produces nothing a creator asked for, so it stays a deployment
    /// decision and no workspace can turn it on.
    /// </remarks>
    public static IReadOnlyList<string> SwitchableTasks { get; } =
    [
        AiTaskCatalog.RecipeConcepts,
        AiTaskCatalog.RecipeFirstDraft,
        AiTaskCatalog.DishFacetSuggestion,
        AiTaskCatalog.RecipeRevision,
        AiTaskCatalog.IngredientSubstitution,
        AiTaskCatalog.RecipeAdaptation,
        AiTaskCatalog.RecipeReview,
        AiTaskCatalog.ProposalExplanation,
        AiTaskCatalog.EditorialPackage,
        AiTaskCatalog.SeoPackage,
        AiTaskCatalog.ChannelPosts,
        AiTaskCatalog.LongFormOutline,
        AiTaskCatalog.LongFormSection,
        AiTaskCatalog.BrandGuideProposal,
        AiTaskCatalog.BrandStyleTestDrive,
        AiTaskCatalog.PhotographyConcept,
        AiTaskCatalog.ImagePrompt,
        AiTaskCatalog.ReferenceImageAnalysis,
    ];

    /// <summary>The catalogue's own spelling of a switchable task, or null when it is not one.</summary>
    public static string? Canonical(string? task) =>
        task is null
            ? null
            : SwitchableTasks.FirstOrDefault(known => string.Equals(known, task.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The whole set of AI features a workspace has switched on.
/// </summary>
/// <remarks>
/// One value rather than a toggle per task: a task named here is on, and every other switchable task is off.
/// That keeps the write idempotent and means a save never depends on what the deployment's default was.
/// </remarks>
public sealed class ReplaceAiFeaturesViewModel
{
    /// <summary>The task discriminators to switch on. An empty list switches every feature off.</summary>
    public IReadOnlyList<string>? Enabled { get; init; }
}

public sealed class ReplaceAiFeaturesViewModelValidator : AbstractValidator<ReplaceAiFeaturesViewModel>
{
    public ReplaceAiFeaturesViewModelValidator() =>
        RuleFor(model => model).Custom((model, context) =>
        {
            if (model.Enabled is null)
            {
                context.AddFailure("enabled", "Send the list of features to switch on. An empty list switches all of them off.");

                return;
            }

            if (model.Enabled.Count > AiFeaturePolicy.SwitchableTasks.Count)
            {
                context.AddFailure("enabled", "That is more features than there are.");

                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var index = 0; index < model.Enabled.Count; index++)
            {
                var canonical = AiFeaturePolicy.Canonical(model.Enabled[index]);

                if (canonical is null)
                {
                    context.AddFailure($"enabled[{index}]", "That is not a feature a workspace can switch.");
                }
                else if (!seen.Add(canonical))
                {
                    context.AddFailure($"enabled[{index}]", "That feature is listed more than once.");
                }
            }
        });
}

/// <summary>One switchable AI feature and where this workspace stands on it.</summary>
/// <param name="Task">The task discriminator, e.g. <c>recipe.revision</c>.</param>
/// <param name="Enabled">Whether a request for it is accepted in this workspace right now.</param>
/// <param name="SetByWorkspace">
/// True once an Owner has saved a choice. False means <paramref name="Enabled"/> is the deployment's default
/// and will follow it until someone here decides.
/// </param>
public sealed record AiFeatureServiceModel(string Task, bool Enabled, bool SetByWorkspace);

/// <summary>Every switchable AI feature, in a stable order.</summary>
public sealed record AiFeaturesServiceModel(IReadOnlyList<AiFeatureServiceModel> Features);

public static class AiFeatureErrors
{
    /// <summary>The request body is not a valid set of features. Maps to 400.</summary>
    public const string Invalid = "ai.features.invalid";

    /// <summary>The caller is not an Owner of this workspace. Maps to 403.</summary>
    public const string Forbidden = "ai.features.forbidden";

    /// <summary>Another Owner saved at the same moment. Nothing was written. Maps to 409.</summary>
    public const string Conflict = "ai.features.conflict";
}

public static class AiFeatureAuditActions
{
    public const string ResourceType = "WorkspaceAiFeatures";

    public const string Replaced = "ai.features.replaced";
}
