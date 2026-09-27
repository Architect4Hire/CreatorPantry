using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-001's request route: what a client may ask for, and what it cannot reach.
/// </summary>
public sealed class AiConceptRequestEndpointTests
{
    // ---- field shape -------------------------------------------------------------------------------------

    /// <summary>
    /// The contract's real content is what is <em>absent</em>. A client names the eleven declared brief
    /// fields; it cannot name a task, a scope, a recipe, a prompt, a model, a provider parameter, a tool
    /// list, or a workspace, because the type has nowhere to put one.
    /// </summary>
    [Fact]
    public void The_request_carries_only_the_eleven_declared_brief_fields()
    {
        var fields = typeof(RequestRecipeConceptsViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Audience", "AvailableIngredients", "Course", "CreatorStyle", "Cuisine", "DietaryGoals",
                "Equipment", "Exclusions", "Season", "Skill", "TimeBudget",
            ],
            fields);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("recipe")]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("workspace")]
    [InlineData("system")]
    public void The_request_cannot_name_a_task_a_recipe_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestRecipeConceptsViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    // ---- validator -----------------------------------------------------------------------------------------

    [Fact]
    public void A_fully_blank_brief_still_validates()
    {
        var result = new RequestRecipeConceptsViewModelValidator().Validate(new RequestRecipeConceptsViewModel());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_fully_populated_brief_validates()
    {
        var result = new RequestRecipeConceptsViewModelValidator().Validate(new RequestRecipeConceptsViewModel
        {
            Audience = "weeknight home cooks",
            Course = "dinner",
            Cuisine = "Sichuan",
            DietaryGoals = "vegetarian",
            AvailableIngredients = "tofu, doubanjiang, scallions",
            Exclusions = "peanuts, shellfish",
            Equipment = "wok, rice cooker",
            Skill = "beginner",
            Season = "winter",
            TimeBudget = "30 minutes",
            CreatorStyle = "playful, kid-friendly",
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_short_field_over_its_bound_is_rejected()
    {
        var result = new RequestRecipeConceptsViewModelValidator().Validate(new RequestRecipeConceptsViewModel
        {
            Cuisine = new string('x', AiPolicy.BriefFieldMaxLength + 1),
        });

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RequestRecipeConceptsViewModel.Cuisine));
    }

    [Fact]
    public void A_list_style_field_over_its_bound_is_rejected()
    {
        var result = new RequestRecipeConceptsViewModelValidator().Validate(new RequestRecipeConceptsViewModel
        {
            Exclusions = new string('x', AiPolicy.BriefListFieldMaxLength + 1),
        });

        Assert.Contains(
            result.Errors, error => error.PropertyName == nameof(RequestRecipeConceptsViewModel.Exclusions));
    }

    // ---- routes ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Requesting_concepts_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Route,
            new RequestRecipeConceptsViewModel { Cuisine = "Sichuan" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_request_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(
            $"{Route}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- idempotency ---------------------------------------------------------------------------------------

    [Fact]
    public void The_idempotency_header_is_the_one_the_rest_of_the_api_uses()
    {
        Assert.Equal("Idempotency-Key", IdempotencyPolicy.KeyHeader);
    }

    /// <summary>Every code this seam can return is namespaced and distinct.</summary>
    [Fact]
    public void The_error_codes_are_stable_and_distinct()
    {
        var codes = typeof(AiConceptRequestErrors).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith("ai.", code, StringComparison.Ordinal));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    private const string Route = "/api/v1/workspaces/workspace-a/recipe-concept-requests";
}
