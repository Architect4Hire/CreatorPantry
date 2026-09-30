using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Which task discriminators a client may name, and which are switched on.</summary>
public sealed class AiTaskOptions
{
    public const string SectionName = "Ai:Tasks";

    /// <summary>
    /// The discriminators this deployment will run. Empty by default, on purpose.
    /// </summary>
    /// <remarks>
    /// The endpoint ships dark. Every request is refused until a deployment opts a task in, so no environment
    /// can start spending a provider budget because a route happened to be deployed. Turning one on is a
    /// configuration change somebody makes deliberately.
    /// </remarks>
    public HashSet<string> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The allow-list a client's task discriminator is resolved through.
/// </summary>
/// <remarks>
/// <para>
/// The client names a task and nothing else. It cannot supply a prompt, a template id, a model, a provider
/// parameter or a tool list, because none of those is a field on the request and none of them is derived from
/// one — the server maps a discriminator it recognises to a task it already knows how to run, or refuses.
/// </para>
/// <para>
/// Two gates, deliberately separate. A discriminator that is not in <see cref="Known"/> is not a task at all
/// and is a bad request; one that is known but not enabled is a real task this deployment has not switched on,
/// which is a different answer and deserves a different code.
/// </para>
/// </remarks>
public static class AiTaskCatalog
{
    /// <summary>The discriminator for the inert task that exercises the lifecycle without calling a model.</summary>
    public const string Diagnostic = "diagnostic";

    /// <summary>The discriminator for AIREC-001's recipe concept generation.</summary>
    public const string RecipeConcepts = "recipe.concepts";

    /// <summary>The discriminator for AIREC-002's structured first-draft generation.</summary>
    public const string RecipeFirstDraft = "recipe.first-draft";

    /// <summary>The discriminator for AIREC-003's scoped revision of an existing recipe.</summary>
    public const string RecipeRevision = "recipe.revision";

    /// <summary>The discriminator for AIREC-004's substitution advice about one selected ingredient.</summary>
    public const string IngredientSubstitution = "recipe.substitution";

    /// <summary>The discriminator for AIREC-005's single-goal recipe adaptation.</summary>
    public const string RecipeAdaptation = "recipe.adaptation";

    /// <summary>The discriminator for AIREC-006's field-linked quality and safety review.</summary>
    public const string RecipeReview = "recipe.review";

    /// <summary>The discriminator for AIREC-008's explanation of an existing proposal.</summary>
    public const string ProposalExplanation = "recipe.proposal-explanation";

    /// <summary>RCPUB-001: the editorial package for one approved recipe version.</summary>
    public const string EditorialPackage = "content.editorial-package";

    /// <summary>RCPUB-002: the SEO package for one approved recipe version.</summary>
    public const string SeoPackage = "content.seo-package";

    private static readonly Dictionary<string, AiTaskType> KnownTasks = new(StringComparer.OrdinalIgnoreCase)
    {
        [Diagnostic] = AiTaskType.Diagnostic,
        [RecipeConcepts] = AiTaskType.RecipeConcepts,
        [RecipeFirstDraft] = AiTaskType.RecipeFirstDraft,
        [RecipeRevision] = AiTaskType.RecipeRevision,
        [IngredientSubstitution] = AiTaskType.IngredientSubstitution,
        [RecipeAdaptation] = AiTaskType.RecipeAdaptation,
        [RecipeReview] = AiTaskType.RecipeReview,
        [ProposalExplanation] = AiTaskType.ProposalExplanation,
        [EditorialPackage] = AiTaskType.EditorialPackage,
        [SeoPackage] = AiTaskType.SeoPackage,
    };

    /// <summary>Every discriminator the server recognises, enabled or not.</summary>
    public static IReadOnlyCollection<string> Known => KnownTasks.Keys;

    /// <summary>
    /// Tasks that cannot run without capability-specific request fields, and so cannot be started through the
    /// generic proposal route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RequestAiProposalViewModel</c> carries a task, a scope and a source version, and writes no
    /// <c>TaskInputsJson</c> at all. Most tasks tolerate that — an unsupplied brief field renders as "not
    /// specified" and the answer is simply less directed. A substitution cannot: it is advice about one named
    /// ingredient, and without that name there is nothing to be advice about.
    /// </para>
    /// <para>
    /// <strong>The scope is the other half of it.</strong> That contract lets the client choose the scope,
    /// and this task's scope is the server's to decide — <see cref="AiOperationScope.Advisory"/> is what makes
    /// "proposes no change to the recipe" a property of the stored row. A request naming <c>WholeRecipe</c>
    /// instead would write a row that contradicts the design, and although the handler refuses such an
    /// operation before any provider call, the row would still be there saying something untrue about it.
    /// </para>
    /// <para>
    /// <see cref="AiTaskType.RecipeAdaptation"/> needs the same carve-out for the same first reason: a goal is
    /// what makes it an adaptation of anything in particular, so it travels through <c>TaskInputsJson</c>
    /// exactly as a substitution's ingredient id does. Its scope is fixed to
    /// <see cref="AiOperationScope.WholeRecipe"/> the same way a substitution's is fixed to
    /// <see cref="AiOperationScope.Advisory"/>.
    /// </para>
    /// <para>
    /// <see cref="AiTaskType.RecipeReview"/> needs the carve-out for the second reason alone, not the first: a
    /// review names no capability-specific field of its own — it reviews the whole pinned recipe, not a
    /// creator-selected part of it — but its scope must still be fixed to
    /// <see cref="AiOperationScope.Advisory"/> by the server, exactly as a substitution's is, rather than left
    /// to a client that could otherwise ask for <c>WholeRecipe</c> on an operation that will never carry a
    /// change.
    /// </para>
    /// <para>
    /// <see cref="AiTaskType.ProposalExplanation"/> needs the carve-out for the first reason alone: it names no
    /// recipe section of its own, only the request id of the proposal it explains, which travels through
    /// <c>TaskInputsJson</c> exactly as a substitution's ingredient id does. Its scope is fixed server-side to
    /// <see cref="AiOperationScope.Advisory"/> the same way, so this is not a second, independent reason for it.
    /// </para>
    /// <para>
    /// <see cref="AiTaskType.RecipeConcepts"/> and <see cref="AiTaskType.RecipeFirstDraft"/> need the carve-out
    /// for a third reason this route cannot satisfy at all: this controller is nested under
    /// <c>/recipes/{recipeId}/ai-proposals</c> and stamps that route segment onto
    /// <see cref="Data.Entities.AiOperation.RecipeId"/> unconditionally, but both tasks name no recipe by design
    /// — their own handlers assert <see cref="AiTaskExecutionContext.RecipeId"/> is null, because a concept or a
    /// first draft is not a change to anything that exists yet. Reaching either through this route would write a
    /// row that contradicts that invariant for as long as the row exists, not just refuse a request that briefly
    /// looked wrong.
    /// </para>
    /// <para>
    /// <see cref="AiTaskType.RecipeRevision"/> needs the carve-out for the first reason: a revision's goal is
    /// optional but, when supplied, is what makes it a revision toward anything in particular, and travels
    /// through <c>TaskInputsJson</c> exactly as an adaptation's goal does. Its own route exists so that field has
    /// somewhere to go; this route has nowhere to put it.
    /// </para>
    /// </remarks>
    public static bool RequiresTaskInputs(AiTaskType task) =>
        task is AiTaskType.IngredientSubstitution or AiTaskType.RecipeAdaptation or AiTaskType.RecipeReview
            or AiTaskType.ProposalExplanation or AiTaskType.RecipeConcepts or AiTaskType.RecipeFirstDraft
            or AiTaskType.RecipeRevision or AiTaskType.EditorialPackage
            or AiTaskType.SeoPackage;

    /// <summary>The task a discriminator names, or null when the server does not recognise it.</summary>
    public static AiTaskType? Resolve(string? discriminator) =>
        discriminator is not null && KnownTasks.TryGetValue(discriminator, out var task) ? task : null;

    public static bool IsEnabled(AiTaskOptions options, string discriminator) =>
        options.Enabled.Contains(discriminator);

    public static AiTaskOptions Bind(IConfiguration configuration)
    {
        var options = new AiTaskOptions();

        foreach (var enabled in configuration.GetSection($"{AiTaskOptions.SectionName}:Enabled").Get<string[]>() ?? [])
        {
            options.Enabled.Add(enabled);
        }

        return options;
    }
}
