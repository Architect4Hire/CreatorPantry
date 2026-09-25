using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The two AI proposal routes: what a client may ask for, what it is told, and what it cannot reach.
/// </summary>
public sealed class AiProposalEndpointTests
{
    /// <summary>
    /// The contract's real content is what is <em>absent</em>. A client names a task and a scope; it cannot
    /// name a prompt, a template, a model, a provider parameter, a tool list, or a workspace, because the type
    /// has nowhere to put one. Asserted by reflection so adding a field is a visible failure rather than a
    /// quiet widening of what a client may steer.
    /// </summary>
    [Fact]
    public void The_request_carries_only_a_task_a_scope_and_a_source_version()
    {
        var fields = typeof(RequestAiProposalViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Scope", "SourceVersionId", "Task"], fields);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("workspace")]
    [InlineData("system")]
    public void The_request_cannot_name_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestAiProposalViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The server maps a discriminator it recognises to a task it knows how to run. An unrecognised one is not
    /// a task at all, so there is no path from client text to a prompt.
    /// </summary>
    [Theory]
    [InlineData("diagnostic", AiTaskType.Diagnostic)]
    [InlineData("DIAGNOSTIC", AiTaskType.Diagnostic)]
    public void A_known_discriminator_resolves_to_a_task(string discriminator, AiTaskType expected)
    {
        Assert.Equal(expected, AiTaskCatalog.Resolve(discriminator));
    }

    [Theory]
    [InlineData("rewrite-everything")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_discriminator_resolves_to_nothing(string? discriminator)
    {
        Assert.Null(AiTaskCatalog.Resolve(discriminator));
    }

    /// <summary>
    /// The endpoint ships dark: nothing is enabled until a deployment says so, so no environment can start
    /// spending a provider budget because a route happened to be deployed.
    /// </summary>
    [Fact]
    public void No_task_is_enabled_by_default()
    {
        Assert.Empty(new AiTaskOptions().Enabled);
        Assert.False(AiTaskCatalog.IsEnabled(new AiTaskOptions(), AiTaskCatalog.Diagnostic));
    }

    [Fact]
    public void A_task_is_enabled_only_by_configuration()
    {
        var options = new AiTaskOptions();
        options.Enabled.Add(AiTaskCatalog.Diagnostic);

        Assert.True(AiTaskCatalog.IsEnabled(options, AiTaskCatalog.Diagnostic));
        Assert.False(AiTaskCatalog.IsEnabled(options, "something-else"));
    }

    // ---- validator -------------------------------------------------------------------------------------

    [Fact]
    public void A_sound_request_validates()
    {
        var result = new RequestAiProposalViewModelValidator().Validate(new RequestAiProposalViewModel
        {
            Task = AiTaskCatalog.Diagnostic,
            Scope = AiOperationScope.WholeRecipe,
            SourceVersionId = Guid.NewGuid(),
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void An_unknown_task_is_rejected_at_the_edge()
    {
        var result = new RequestAiProposalViewModelValidator().Validate(new RequestAiProposalViewModel
        {
            Task = "invent-a-recipe",
            Scope = AiOperationScope.WholeRecipe,
            SourceVersionId = Guid.NewGuid(),
        });

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RequestAiProposalViewModel.Task));
    }

    /// <summary>An undeclared scope would permit nothing downstream, so it is refused where it is readable.</summary>
    [Fact]
    public void An_undeclared_scope_is_rejected()
    {
        var result = new RequestAiProposalViewModelValidator().Validate(new RequestAiProposalViewModel
        {
            Task = AiTaskCatalog.Diagnostic,
            Scope = AiOperationScope.Unspecified,
            SourceVersionId = Guid.NewGuid(),
        });

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RequestAiProposalViewModel.Scope));
    }

    /// <summary>
    /// A proposal computed against "latest" cannot be checked for staleness later, which is the whole basis of
    /// accepting one safely.
    /// </summary>
    [Fact]
    public void A_missing_source_version_is_rejected()
    {
        var result = new RequestAiProposalViewModelValidator().Validate(new RequestAiProposalViewModel
        {
            Task = AiTaskCatalog.Diagnostic,
            Scope = AiOperationScope.WholeRecipe,
            SourceVersionId = Guid.Empty,
        });

        Assert.Contains(
            result.Errors,
            error => error.PropertyName == nameof(RequestAiProposalViewModel.SourceVersionId));
    }

    // ---- routes ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Requesting_a_proposal_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Route(Guid.NewGuid()),
            new RequestAiProposalViewModel
            {
                Task = AiTaskCatalog.Diagnostic,
                Scope = AiOperationScope.WholeRecipe,
                SourceVersionId = Guid.NewGuid(),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_proposal_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(
            $"{Route(Guid.NewGuid())}/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Generation spends a provider budget, so a retried request must be able to return the first answer
    /// rather than buy a second. The header is required rather than optional for that reason.
    /// </summary>
    [Fact]
    public void The_idempotency_header_is_the_one_the_rest_of_the_api_uses()
    {
        Assert.Equal("Idempotency-Key", IdempotencyPolicy.KeyHeader);
    }

    /// <summary>Every code this seam can return is namespaced and distinct.</summary>
    [Fact]
    public void The_error_codes_are_stable_and_distinct()
    {
        var codes = typeof(AiProposalErrors).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith("ai.", code, StringComparison.Ordinal));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    private static string Route(Guid recipeId) =>
        $"/api/v1/workspaces/workspace-a/recipes/{recipeId}/ai-proposals";
}
