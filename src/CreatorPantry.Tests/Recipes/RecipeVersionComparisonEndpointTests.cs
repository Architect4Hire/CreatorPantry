using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions/compare</c> through the real Gateway —
/// real cookie session, real gateway-signed internal token, real API — for the published shape, what the
/// comparison refuses to hand back, and what one workspace can learn about the other's archives.
/// </summary>
public sealed class RecipeVersionComparisonEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string CompareOf(SeededWorkspace workspace, Guid recipeId, string query) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/versions/compare{query}";

    // ---- The published comparison ----

    [Fact]
    public async Task An_edit_is_published_as_a_field_change_in_the_section_it_belongs_to()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);
        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Olive oil and rosemary cake");

        var body = await ReadAsync(await client.GetAsync(
            CompareOf(_fixture.WorkspaceA, recipeId, "?from=1&to=2"), TestContext.Current.CancellationToken));

        Assert.Equal(1, body.GetProperty("from").GetProperty("versionNumber").GetInt32());
        Assert.Equal(2, body.GetProperty("to").GetProperty("versionNumber").GetInt32());

        var comparison = body.GetProperty("comparison");
        Assert.True(comparison.GetProperty("hasChanges").GetBoolean());

        var change = SectionOf(comparison, "Metadata").GetProperty("fieldChanges").EnumerateArray().Single();
        Assert.Equal("Title", change.GetProperty("field").GetString());
        Assert.Equal("Olive oil cake", change.GetProperty("from").GetString());
        Assert.Equal("Olive oil and rosemary cake", change.GetProperty("to").GetString());
    }

    /// <summary>
    /// Every section is published whether or not it changed, so a comparison panel renders one stable frame
    /// and "nothing changed in the timing" is something the answer says rather than something a client infers
    /// from an absence.
    /// </summary>
    [Fact]
    public async Task Every_section_is_published_in_a_fixed_order()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);
        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Olive oil and rosemary cake");

        var body = await ReadAsync(await client.GetAsync(
            CompareOf(_fixture.WorkspaceA, recipeId, "?from=1&to=2"), TestContext.Current.CancellationToken));

        Assert.Equal(
            Enum.GetNames<RecipeComparisonSection>(),
            body.GetProperty("comparison").GetProperty("sections").EnumerateArray()
                .Select(section => section.GetProperty("section").GetString())
                .ToArray());
    }

    [Fact]
    public async Task Comparing_a_version_with_itself_reports_no_changes()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            CompareOf(_fixture.WorkspaceA, recipeId, "?from=1&to=1"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var comparison = (await ReadAsync(response)).GetProperty("comparison");

        // A client that preselects the same version on both sides renders "no changes" rather than handling
        // an error, which is why the validator accepts it.
        Assert.False(comparison.GetProperty("hasChanges").GetBoolean());
        Assert.All(
            comparison.GetProperty("sections").EnumerateArray(),
            section => Assert.False(section.GetProperty("hasChanges").GetBoolean()));
    }

    [Fact]
    public async Task The_direction_is_the_callers_and_reverses_the_reading()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);
        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Olive oil and rosemary cake");

        // What a creator weighing a revert is asking: read the newer version as the left-hand side.
        var body = await ReadAsync(await client.GetAsync(
            CompareOf(_fixture.WorkspaceA, recipeId, "?from=2&to=1"), TestContext.Current.CancellationToken));

        Assert.Equal(2, body.GetProperty("from").GetProperty("versionNumber").GetInt32());

        var change = SectionOf(body.GetProperty("comparison"), "Metadata")
            .GetProperty("fieldChanges").EnumerateArray().Single();

        Assert.Equal("Olive oil and rosemary cake", change.GetProperty("from").GetString());
        Assert.Equal("Olive oil cake", change.GetProperty("to").GetString());
    }

    /// <summary>
    /// The snapshots are never returned. A client that held both could derive a diff of its own and disagree
    /// with the server about what changed, which is the whole reason this route computes one.
    /// </summary>
    [Fact]
    public async Task The_archived_documents_are_never_published()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, headnote: "The one my grandmother made.");
        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Olive oil and rosemary cake");

        var response = await client.GetAsync(
            CompareOf(_fixture.WorkspaceA, recipeId, "?from=1&to=2"), TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("document", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schemaVersion", raw, StringComparison.OrdinalIgnoreCase);

        // The headnote never changed, so no field change mentions it — and since the documents are absent,
        // it appears nowhere at all.
        Assert.DoesNotContain("grandmother", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_member_including_a_viewer_may_compare_versions()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB);
        await EditAsync(owner, _fixture.WorkspaceB, recipeId, "Olive oil and rosemary cake");

        using var member = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await member.GetAsync(
            CompareOf(_fixture.WorkspaceB, recipeId, "?from=1&to=2"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Refusals ----

    [Theory]
    [InlineData("?to=2")]
    [InlineData("?from=1")]
    [InlineData("?from=0&to=2")]
    [InlineData("?from=1&to=-3")]
    public async Task A_query_that_does_not_name_two_version_numbers_is_refused(string query)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            CompareOf(_fixture.WorkspaceA, recipeId, query), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.ComparisonInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A version number this recipe does not have is its own refusal, naming the parameter at fault. Safe to
    /// disclose: the caller has already been shown that the recipe exists and may list its versions, so
    /// "there is no version 9" tells them nothing they could not read for themselves.
    /// </summary>
    [Fact]
    public async Task A_version_number_the_recipe_does_not_have_names_the_parameter_at_fault()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            CompareOf(_fixture.WorkspaceA, recipeId, "?from=1&to=9"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("to", out _));
        Assert.False(body.GetProperty("errors").TryGetProperty("from", out _));
    }

    [Fact]
    public async Task Both_missing_version_numbers_are_reported_together()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.GetAsync(
            CompareOf(_fixture.WorkspaceA, recipeId, "?from=8&to=9"), TestContext.Current.CancellationToken));

        // Both, so a creator who mistyped both numbers is not sent round the loop twice.
        Assert.True(body.GetProperty("errors").TryGetProperty("from", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("to", out _));
    }

    // ---- Isolation ----

    /// <summary>
    /// An unknown recipe and another workspace's recipe answer identically, so that a caller cannot learn a
    /// recipe exists by being refused it (tenancy.md). Distinct from the version refusal above, which is only
    /// reachable once the recipe has been shown to be readable.
    /// </summary>
    [Fact]
    public async Task An_unknown_recipe_and_another_workspaces_recipe_answer_identically()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB);
        await EditAsync(ownerB, _fixture.WorkspaceB, recipeInB, "Olive oil and rosemary cake");

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        var token = TestContext.Current.CancellationToken;

        var unknown = await ownerA.GetAsync(CompareOf(_fixture.WorkspaceA, Guid.NewGuid(), "?from=1&to=2"), token);
        var othersRecipe = await ownerA.GetAsync(CompareOf(_fixture.WorkspaceA, recipeInB, "?from=1&to=2"), token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersRecipe.StatusCode);

        var unknownBody = await ReadAsync(unknown);
        var othersBody = await ReadAsync(othersRecipe);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknownBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTrace(unknownBody), WithoutTrace(othersBody));
    }

    /// <summary>
    /// Naming another workspace's recipe under its own slug is refused at the workspace boundary, before any
    /// version number is looked at.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_compare_versions_through_the_other_slug()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB);
        await EditAsync(ownerB, _fixture.WorkspaceB, recipeInB, "Olive oil and rosemary cake");

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var response = await ownerA.GetAsync(
            CompareOf(_fixture.WorkspaceB, recipeInB, "?from=1&to=2"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Two recipes in two workspaces both have a version 1 and a version 2, and each workspace's comparison
    /// describes only its own. The case a version number can make worse than an id could: numbers collide
    /// across recipes by design, so the recipe predicate is what keeps one archive out of the other's answer.
    /// </summary>
    [Fact]
    public async Task Identical_version_numbers_in_two_workspaces_compare_independently()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var token = TestContext.Current.CancellationToken;

        var recipeInA = await CreateAsync(ownerA, _fixture.WorkspaceA, "Workspace A cake");
        await EditAsync(ownerA, _fixture.WorkspaceA, recipeInA, "Workspace A cake, revised");

        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB, "Workspace B cake");
        await EditAsync(ownerB, _fixture.WorkspaceB, recipeInB, "Workspace B cake, revised");

        var bodyA = await ReadAsync(await ownerA.GetAsync(CompareOf(_fixture.WorkspaceA, recipeInA, "?from=1&to=2"), token));
        var bodyB = await ReadAsync(await ownerB.GetAsync(CompareOf(_fixture.WorkspaceB, recipeInB, "?from=1&to=2"), token));

        Assert.Equal("Workspace A cake", TitleChange(bodyA).GetProperty("from").GetString());
        Assert.Equal("Workspace B cake", TitleChange(bodyB).GetProperty("from").GetString());

        // Different archives, so different version rows, however alike the numbers look.
        Assert.NotEqual(
            bodyA.GetProperty("from").GetProperty("versionId").GetGuid(),
            bodyB.GetProperty("from").GetProperty("versionId").GetGuid());
    }

    // ---- Helpers ----

    private static JsonElement SectionOf(JsonElement comparison, string section) =>
        comparison.GetProperty("sections").EnumerateArray()
            .Single(entry => entry.GetProperty("section").GetString() == section);

    private static JsonElement TitleChange(JsonElement body) =>
        SectionOf(body.GetProperty("comparison"), "Metadata")
            .GetProperty("fieldChanges").EnumerateArray()
            .Single(change => change.GetProperty("field").GetString() == "Title");

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<Guid> CreateAsync(
        GatewayClient client, SeededWorkspace workspace, string title = "Olive oil cake", string? headnote = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title, headnote },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await ReadAsync(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>
    /// Edits the recipe, which is the only way to write a second version — reading the current token first,
    /// because an edit must quote the state it was composed against.
    /// </summary>
    private static async Task EditAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, string title)
    {
        var token = TestContext.Current.CancellationToken;
        var route = $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

        var detail = await ReadAsync(await client.GetAsync(route, token));

        var response = await client.PatchAsJsonAsync(
            route,
            new { expectedConcurrencyToken = detail.GetProperty("concurrencyToken").GetString(), title },
            token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A problem body with its trace id removed, so two refusals can be compared for being the same refusal
    /// rather than for having happened in the same request.
    /// </summary>
    private static string WithoutTrace(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => !property.Name.Contains("traceId", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("correlation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property.Name, property => property.Value.ToString()));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
