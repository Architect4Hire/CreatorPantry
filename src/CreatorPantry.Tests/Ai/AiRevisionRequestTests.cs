extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-003's request contract: what a creator may ask for, and what the route refuses before a model is
/// ever called.
/// </summary>
public sealed class AiRevisionRequestTests
{
    private const string Route = "/api/v1/workspaces/workspace-a/recipes/11111111-1111-1111-1111-111111111111/revision-requests";

    // ---- field shape -------------------------------------------------------------------------------------

    /// <summary>
    /// A section, a version, and what the creator wants. No task discriminator — this route is the task — and
    /// crucially no field list: which fields a section permits is the server's answer and the bound it
    /// enforces, not something a request may widen.
    /// </summary>
    [Fact]
    public void The_request_carries_a_scope_a_source_version_and_a_goal()
    {
        var fields = typeof(RequestRecipeRevisionViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Goal", "Scope", "SourceVersionId"], fields);
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
    [InlineData("field")]
    public void The_request_cannot_name_a_task_a_field_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestRecipeRevisionViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    // ---- validator ---------------------------------------------------------------------------------------

    private static FluentValidation.Results.ValidationResult Validate(RequestRecipeRevisionViewModel model) =>
        new RequestRecipeRevisionViewModelValidator().Validate(model);

    private static RequestRecipeRevisionViewModel Request(
        AiOperationScope scope = AiOperationScope.Metadata, string? goal = "Make it punchier") =>
        new() { Scope = scope, SourceVersionId = Guid.NewGuid(), Goal = goal };

    [Theory]
    [InlineData(AiOperationScope.WholeRecipe)]
    [InlineData(AiOperationScope.Metadata)]
    [InlineData(AiOperationScope.Instructions)]
    [InlineData(AiOperationScope.Ingredients)]
    public void A_revisable_section_validates(AiOperationScope scope)
    {
        Assert.True(Validate(Request(scope)).IsValid);
    }

    [Fact]
    public void A_revision_with_no_goal_is_a_real_request()
    {
        Assert.True(Validate(Request(goal: null)).IsValid);
    }

    /// <summary>
    /// A scope that defaulted to everything would make the bound something a creator had to remember to ask
    /// for, which is the opposite of what this capability is.
    /// </summary>
    [Fact]
    public void A_request_that_names_no_section_is_refused()
    {
        var model = Request();
        model.Scope = AiOperationScope.Unspecified;

        Assert.False(Validate(model).IsValid);
    }

    /// <summary>
    /// Media has no applicable change, so a revision scoped to it would spend a provider budget to produce an
    /// answer the validator then refused entirely. Refused at the edge instead, before anything is queued.
    /// </summary>
    [Theory]
    [InlineData(AiOperationScope.Media)]
    [InlineData(AiOperationScope.NotApplicable)]
    [InlineData(AiOperationScope.Advisory)]
    public void A_section_nothing_can_be_applied_in_is_refused_before_anything_is_queued(AiOperationScope scope)
    {
        var result = Validate(Request(scope));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.ErrorMessage.Contains("cannot be revised"));
    }

    [Fact]
    public void A_scope_outside_the_enum_is_refused()
    {
        var model = Request();
        model.Scope = (AiOperationScope)99;

        Assert.False(Validate(model).IsValid);
    }

    /// <summary>
    /// Required, for the reason recipes.md gives every version reference: a revision computed against
    /// "latest" cannot be checked for staleness later, and that check is what makes accepting one safe.
    /// </summary>
    [Fact]
    public void A_request_that_pins_no_version_is_refused()
    {
        var model = Request();
        model.SourceVersionId = Guid.Empty;

        Assert.False(Validate(model).IsValid);
    }

    [Fact]
    public void A_goal_over_its_bound_is_refused()
    {
        Assert.False(Validate(Request(goal: new string('x', AiPolicy.RevisionGoalMaxLength + 1))).IsValid);
    }

    // ---- what the model is told about its bound ----------------------------------------------------------

    /// <summary>
    /// The instructions are derived from the same tables that enforce the bound, so they cannot describe a
    /// bound the validator does not apply — and a creator scoped to one section never has the wider
    /// vocabulary shown to the model with a request to restrain itself.
    /// </summary>
    [Fact]
    public void The_scope_the_template_states_is_the_scope_the_validator_applies()
    {
        foreach (var scope in AiRevisionScopes.Revisable)
        {
            foreach (var target in AiPolicy.AllowedTargets(scope))
            {
                foreach (var field in AiPolicy.AllowedFields(scope, target))
                {
                    Assert.True(
                        AiDiffFields.IsSettable(target, field),
                        $"{scope}/{target}/{field} is offered but is not a settable field.");
                }
            }
        }
    }

    /// <summary>
    /// Every field the scope permits is one the recipe seam can actually take a value for — so a creator is
    /// never offered a change that would be refused at acceptance.
    /// </summary>
    [Fact]
    public void Every_field_a_scope_permits_carries_a_value_bound()
    {
        foreach (var scope in AiRevisionScopes.Revisable)
        {
            foreach (var target in AiPolicy.AllowedTargets(scope))
            {
                foreach (var field in AiPolicy.AllowedFields(scope, target))
                {
                    Assert.True(
                        ProposedRecipeValues.MaxLength(field) > 0,
                        $"{field} has no bound of its own.");
                }
            }
        }
    }

    // ---- error codes -------------------------------------------------------------------------------------

    [Fact]
    public void The_error_codes_are_stable_and_distinct()
    {
        var codes = typeof(AiRevisionRequestErrors).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith("ai.", code, StringComparison.Ordinal));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(nameof(AiRevisionRequestErrors.RequestInvalid), 400)]
    [InlineData(nameof(AiRevisionRequestErrors.TaskNotEnabled), 400)]
    [InlineData(nameof(AiRevisionRequestErrors.SourceVersionInvalid), 400)]
    [InlineData(nameof(AiRevisionRequestErrors.RecipeNotFound), 404)]
    [InlineData(nameof(AiRevisionRequestErrors.RequestNotFound), 404)]
    public void Each_error_code_maps_to_the_status_the_route_documents(string name, int expected)
    {
        var code = (string)typeof(AiRevisionRequestErrors).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(expected, ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code));
    }

    // ---- the route ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Requesting_a_revision_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Route, Request(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_revision_request_without_a_session_is_refused()
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
