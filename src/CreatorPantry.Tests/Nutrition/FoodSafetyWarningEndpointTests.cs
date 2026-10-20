using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// <c>GET .../recipes/{recipeId}/versions/{versionNumber}/analysis/food-safety</c> through the real Gateway
/// (14.7, H-3): the high-risk method warnings 14.5 computes, which until this route reached no creator at all.
/// </summary>
/// <remarks>
/// The audit's finding was about reach, not wording: the detector, its guard tests and its disclaimer were all
/// in place, and nothing served them. A recipe the detector flags for canning could be approved and published
/// with no warning shown anywhere. These tests cover the route, its two refusals, and what another workspace
/// can see.
/// </remarks>
public sealed class FoodSafetyWarningEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_member_reads_the_warnings_with_their_triggers_and_the_disclaimer()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(
            client,
            _fixture.WorkspaceA,
            "Pressure-can the jars at 10 lb for 75 minutes, then store them in the pantry.");

        var response = await ReadAsync(client, _fixture.WorkspaceA, recipeId, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "the vocabulary it reads changes as it is curated");

        var body = await BodyOf(response);
        Assert.Equal(recipeId, body.GetProperty("recipeId").GetGuid());
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.Equal(FoodSafetyWarningPolicy.MethodVersion, body.GetProperty("methodVersion").GetString());
        Assert.Equal(FoodSafetyWarningPolicy.Disclaimer, body.GetProperty("disclaimer").GetString());

        var canning = Assert.Single(
            body.GetProperty("warnings").EnumerateArray(),
            warning => warning.GetProperty("category").GetString() == nameof(FoodSafetyCategory.Canning));

        Assert.Equal(nameof(FoodSafetyConfidence.Detected), canning.GetProperty("confidence").GetString());

        // Nothing is approved to cite (B-33), so every warning says a reference still has to be checked.
        var evidence = canning.GetProperty("evidence");
        Assert.True(evidence.GetProperty("verificationRequired").GetBoolean());
        Assert.Equal(nameof(FoodSafetyEvidenceStatus.NoApprovedSource), evidence.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, evidence.GetProperty("citation").ValueKind);

        // The creator's own words are what the trigger quotes, and the step is where it was found.
        var trigger = Assert.Single(canning.GetProperty("triggers").EnumerateArray());
        Assert.Equal(nameof(FoodSafetyField.Step), trigger.GetProperty("field").GetString());
        Assert.Contains("can", trigger.GetProperty("matched").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A recipe with nothing to flag still answers, and still carries the disclaimer.
    /// </summary>
    /// <remarks>
    /// The case the disclaimer exists for: an empty list is not a statement that the recipe is safe, and a
    /// screen reading this route has to be given the sentence that says so.
    /// </remarks>
    [Fact]
    public async Task A_recipe_with_nothing_flagged_answers_with_an_empty_list_and_the_disclaimer()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, "Stir the flour and sugar together, then bake.");

        var body = await BodyOf(await ReadAsync(client, _fixture.WorkspaceA, recipeId, 1));

        Assert.Empty(body.GetProperty("warnings").EnumerateArray());
        Assert.Equal(FoodSafetyWarningPolicy.Disclaimer, body.GetProperty("disclaimer").GetString());
        Assert.Equal(0, body.GetProperty("unrecognisedTechniques").GetInt32());
    }

    /// <remarks>
    /// Any member below Owner, as the sibling analysis routes allow: reading what a recipe's own text says is
    /// not an edit, and a warning nobody may read is the state this route was added to end.
    /// </remarks>
    [Fact]
    public async Task A_member_below_owner_may_read_the_warnings()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceA, "Ferment the cabbage in brine for two weeks.");

        using var member = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: Ct);
        var response = await ReadAsync(member, _fixture.WorkspaceA, recipeId, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_version_number_is_not_found_and_names_the_number()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, "Stir and bake.");

        var response = await ReadAsync(client, _fixture.WorkspaceA, recipeId, 9);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("recipes.version.not_found", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("versionNumber", out _));
    }

    /// <summary>
    /// Workspace B cannot read workspace A's recipe, and gets the answer an unknown recipe gets.
    /// </summary>
    /// <remarks>
    /// Two workspaces, as every workspace-scoped feature owes (tenancy.md): the same 404 for "not yours" and
    /// "does not exist", so the route discloses nothing about what workspace A has.
    /// </remarks>
    [Fact]
    public async Task Another_workspace_is_told_the_recipe_does_not_exist()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(ownerA, _fixture.WorkspaceA, "Pressure-can the jars at 10 lb for 75 minutes.");

        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);

        // Asked for in B's own workspace, where the recipe is not; and at A's route, which B is not in.
        var inB = await ReadAsync(ownerB, _fixture.WorkspaceB, recipeId, 1);
        Assert.Equal(HttpStatusCode.NotFound, inB.StatusCode);
        Assert.Equal("recipes.recipe.not_found", (await BodyOf(inB)).GetProperty("code").GetString());

        var atA = await ReadAsync(ownerB, _fixture.WorkspaceA, recipeId, 1);
        Assert.Equal(HttpStatusCode.NotFound, atA.StatusCode);
    }

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await ReadAsync(client, _fixture.WorkspaceA, Guid.NewGuid(), 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Plumbing ----

    private static string Route(SeededWorkspace workspace, Guid recipeId, int versionNumber) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/versions/{versionNumber}/analysis/food-safety";

    private static Task<HttpResponseMessage> ReadAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, int versionNumber) =>
        client.GetAsync(Route(workspace, recipeId, versionNumber), Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<Guid> CreateAsync(GatewayClient client, SeededWorkspace workspace, string step)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new
            {
                title = "Pantry jars",
                ingredientGroups = new[]
                {
                    new { ingredients = new[] { new { displayText = "500 g tomatoes" } } },
                },
                instructions = new object[] { new { steps = new object[] { new { text = step } } } },
            },
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct)))
            .GetProperty("id").GetGuid();
    }
}
