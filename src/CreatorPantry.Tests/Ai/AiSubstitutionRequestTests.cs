extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-004's request contract: what a creator may ask for, and what the route refuses before a model is
/// ever called.
/// </summary>
public sealed class AiSubstitutionRequestTests
{
    private const string Route =
        "/api/v1/workspaces/workspace-a/recipes/11111111-1111-1111-1111-111111111111/substitution-requests";

    // ---- field shape -------------------------------------------------------------------------------------

    /// <summary>
    /// A version, an ingredient, and why. No task discriminator — this route is the task — and no scope,
    /// because a substitution proposes no change and there is no bound for a creator to choose.
    /// </summary>
    [Fact]
    public void The_request_carries_a_source_version_an_ingredient_and_a_reason()
    {
        var fields = typeof(RequestIngredientSubstitutionViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["IngredientId", "Reason", "SourceVersionId"], fields);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("workspace")]
    [InlineData("system")]
    [InlineData("scope")]
    public void The_request_cannot_name_a_task_a_scope_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestIngredientSubstitutionViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    // ---- validator ---------------------------------------------------------------------------------------

    private static FluentValidation.Results.ValidationResult Validate(
        RequestIngredientSubstitutionViewModel model) =>
        new RequestIngredientSubstitutionViewModelValidator().Validate(model);

    private static RequestIngredientSubstitutionViewModel Request(string? reason = "I am out of buttermilk") =>
        new() { SourceVersionId = Guid.NewGuid(), IngredientId = Guid.NewGuid(), Reason = reason };

    [Fact]
    public void A_well_formed_request_validates()
    {
        Assert.True(Validate(Request()).IsValid);
    }

    /// <summary>
    /// "What else would work here?" is a complete question. An absent reason is also the safer default: it is
    /// a field the model is told not to guess at rather than one it is invited to fill in.
    /// </summary>
    [Fact]
    public void A_request_with_no_reason_is_a_real_request()
    {
        Assert.True(Validate(Request(reason: null)).IsValid);
    }

    [Fact]
    public void A_request_that_pins_no_version_is_refused()
    {
        var model = Request();
        model.SourceVersionId = Guid.Empty;

        Assert.False(Validate(model).IsValid);
    }

    [Fact]
    public void A_request_that_names_no_ingredient_is_refused()
    {
        var model = Request();
        model.IngredientId = Guid.Empty;

        Assert.False(Validate(model).IsValid);
    }

    [Fact]
    public void A_reason_over_its_bound_is_refused()
    {
        Assert.False(
            Validate(Request(reason: new string('x', AiPolicy.SubstitutionReasonMaxLength + 1))).IsValid);
    }

    // ---- the task and the bound it runs in ---------------------------------------------------------------

    [Fact]
    public void The_discriminator_resolves_to_the_substitution_task()
    {
        Assert.Equal(
            AiTaskType.IngredientSubstitution,
            AiTaskCatalog.Resolve(AiTaskCatalog.IngredientSubstitution));
    }

    /// <summary>
    /// A substitution is never offered as a revisable section. <c>AiRevisionScopes.Revisable</c> derives from
    /// what can actually be applied, and nothing can be applied in an advisory scope.
    /// </summary>
    [Fact]
    public void Advisory_is_not_a_section_a_revision_can_be_scoped_to()
    {
        Assert.DoesNotContain(AiOperationScope.Advisory, AiRevisionScopes.Revisable);
    }

    /// <summary>
    /// The generic proposal route cannot start this task, and that is a guard rather than a tidiness rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That contract writes no task inputs, so a substitution started there names no ingredient and has
    /// nothing to be advice about. More to the point, it lets the client choose the scope — and this task's
    /// scope is the server's to decide. A request naming <c>WholeRecipe</c> would store a row saying
    /// something untrue about an operation that proposes no change at all, even though the handler refuses it
    /// before any provider call.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_generic_proposal_route_cannot_start_this_task()
    {
        Assert.True(AiTaskCatalog.RequiresTaskInputs(AiTaskType.IngredientSubstitution));
    }

    /// <summary>
    /// And the one task with no dedicated route of its own — the inert lifecycle exerciser — still can, so the
    /// guard did not quietly narrow a shipped route.
    /// </summary>
    [Fact]
    public void The_task_with_no_dedicated_route_is_unaffected()
    {
        Assert.False(AiTaskCatalog.RequiresTaskInputs(AiTaskType.Diagnostic));
    }

    /// <summary>
    /// Concepts, first draft, and revision each shipped their own route later, and the generic one still
    /// answered to their discriminator until it was closed off here — this pins the closed state so it cannot
    /// regress silently.
    /// </summary>
    /// <remarks>
    /// A concept or a first draft names no recipe by design (their handlers assert
    /// <see cref="AiTaskExecutionContext.RecipeId"/> is null), but this route is nested under
    /// <c>/recipes/{recipeId}/ai-proposals</c> and would stamp one anyway. A revision's optional goal has
    /// nowhere to travel on this contract at all.
    /// </remarks>
    [Theory]
    [InlineData(AiTaskType.RecipeConcepts)]
    [InlineData(AiTaskType.RecipeFirstDraft)]
    [InlineData(AiTaskType.RecipeRevision)]
    public void Tasks_with_their_own_route_cannot_start_from_the_generic_one(AiTaskType task)
    {
        Assert.True(AiTaskCatalog.RequiresTaskInputs(task));
    }

    /// <summary>
    /// The template's declared inputs are the ones the handler renders. The two are joined only by these
    /// strings, and a mismatch is caught at startup by the loader rather than at the first generation.
    /// </summary>
    [Fact]
    public void The_template_declares_exactly_the_inputs_the_handler_supplies()
    {
        var template = Domain.Managers.Prompts.EmbeddedPromptTemplateStore
            .Load(typeof(AiPolicy).Assembly)
            .Get(AiTaskCatalog.IngredientSubstitution);

        Assert.Equal(
            ["maxAlternatives", "selectedIngredient"],
            template.Inputs.Select(input => input.Name).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The safety class is declared and is the one this capability's cautions are about. A template that
    /// claimed <c>None</c> would be describing a different capability.
    /// </summary>
    [Fact]
    public void The_template_declares_a_dietary_or_allergen_safety_class()
    {
        var template = Domain.Managers.Prompts.EmbeddedPromptTemplateStore
            .Load(typeof(AiPolicy).Assembly)
            .Get(AiTaskCatalog.IngredientSubstitution);

        Assert.Equal(Domain.Managers.Prompts.PromptSafetyClass.DietaryOrAllergen, template.SafetyClass);
    }

    // ---- error codes -------------------------------------------------------------------------------------

    [Fact]
    public void The_error_codes_are_stable_and_distinct()
    {
        var codes = typeof(AiSubstitutionRequestErrors).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith("ai.", code, StringComparison.Ordinal));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// An unknown ingredient answers 400 rather than 404, because the ingredient is not the resource this
    /// route addresses — the recipe is. See <see cref="AiSubstitutionRequestErrors.IngredientInvalid"/>.
    /// </summary>
    [Theory]
    [InlineData(nameof(AiSubstitutionRequestErrors.RequestInvalid), 400)]
    [InlineData(nameof(AiSubstitutionRequestErrors.TaskNotEnabled), 400)]
    [InlineData(nameof(AiSubstitutionRequestErrors.SourceVersionInvalid), 400)]
    [InlineData(nameof(AiSubstitutionRequestErrors.IngredientInvalid), 400)]
    [InlineData(nameof(AiSubstitutionRequestErrors.RecipeNotFound), 404)]
    [InlineData(nameof(AiSubstitutionRequestErrors.RequestNotFound), 404)]
    public void Each_error_code_maps_to_the_status_the_route_documents(string name, int expected)
    {
        var code = (string)typeof(AiSubstitutionRequestErrors).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(expected, ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code));
    }

    // ---- the route ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Requesting_substitutions_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_substitution_request_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync($"{Route}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void The_idempotency_header_is_the_one_the_rest_of_the_api_uses()
    {
        Assert.Equal("Idempotency-Key", IdempotencyPolicy.KeyHeader);
    }
}
