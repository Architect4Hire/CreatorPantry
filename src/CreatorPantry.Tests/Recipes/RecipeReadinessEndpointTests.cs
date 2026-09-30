using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{slug}/recipes/{recipeId}/readiness</c> through the real Gateway — real cookie
/// session, real gateway-signed internal token, real API — for the published shape, who may read it, what it
/// says about the moment it describes, and what one workspace can learn about the other's recipes.
/// </summary>
/// <remarks>
/// The rules themselves are <see cref="RecipeReadinessEvaluatorTests"/>, which covers every one of them without
/// a database. What this file is for is the route: the status codes, the JSON a client actually receives, the
/// headers, and the isolation the evaluator cannot assert because it never sees a workspace.
/// </remarks>
public sealed class RecipeReadinessEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string ReadinessIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/readiness";

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

    // ---- The published shape ----

    /// <summary>
    /// The emptiest recipe the route can be asked about is evaluated rather than refused. A creator who has
    /// just started typing must see what is outstanding, not an error.
    /// </summary>
    [Fact]
    public async Task A_bare_recipe_is_evaluated_rather_than_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("hasBlockers").GetBoolean());
        Assert.True(body.GetProperty("blockerCount").GetInt32() > 0);
    }

    /// <summary>
    /// Every rule that ran is reported, satisfied ones included — the thing this feeds is a checklist, and a
    /// creator needs to see what they have cleared as much as what they have not.
    /// </summary>
    [Fact]
    public async Task Every_rule_in_the_catalogue_is_reported_with_a_verdict()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));

        var reported = body.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("ruleId").GetString()!)
            .ToList();

        Assert.Equal(
            [.. RecipeReadinessCatalogue.Rules.Select(rule => rule.Id).Order(StringComparer.Ordinal)],
            [.. reported.Order(StringComparer.Ordinal)]);

        // Nothing switched off and nothing misnamed in this deployment's configuration, and both are published
        // rather than inferred from the checklist's length.
        Assert.Empty(body.GetProperty("disabledRuleIds").EnumerateArray());
        Assert.Empty(body.GetProperty("unknownConfiguredRuleIds").EnumerateArray());
        Assert.Equal(RecipeReadinessCatalogue.Version, body.GetProperty("ruleSetVersion").GetString());
    }

    /// <summary>
    /// The restriction this route exists under: an unmet rule says which record and which field caused it.
    /// "Your recipe has no ingredients" is not that; the same message plus the recipe id and
    /// <c>IngredientLines</c> is.
    /// </summary>
    [Fact]
    public async Task An_unmet_rule_names_the_record_and_the_field_behind_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var finding = await FindingFor(
            client, _fixture.WorkspaceA, recipeId, RecipeReadinessCatalogue.IngredientsPresent);

        Assert.Equal(nameof(RecipeReadinessStatus.Blocker), finding.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(finding.GetProperty("detail").GetString()));

        var evidence = Assert.Single(finding.GetProperty("evidence").EnumerateArray());
        Assert.Equal(nameof(RecipeReadinessEvidenceKind.Recipe), evidence.GetProperty("kind").GetString());
        Assert.Equal(recipeId, evidence.GetProperty("recordId").GetGuid());
    }

    /// <summary>
    /// Evidence quotes the creator's own words where there are any to quote, so a reader sees the line rather
    /// than its id. Never a normalized form (recipes.md).
    /// </summary>
    [Fact]
    public async Task Evidence_quotes_the_creators_own_line_text()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        await AddIngredientAsync(client, _fixture.WorkspaceA, recipeId, "a handful of something nobody has catalogued");

        var finding = await FindingFor(
            client, _fixture.WorkspaceA, recipeId, RecipeReadinessCatalogue.IngredientsUnrecognized);

        var evidence = Assert.Single(finding.GetProperty("evidence").EnumerateArray());

        Assert.Equal(
            "a handful of something nobody has catalogued",
            evidence.GetProperty("label").GetString());
        Assert.Equal(
            nameof(RecipeReadinessEvidenceKind.RecipeIngredientLine),
            evidence.GetProperty("kind").GetString());
    }

    /// <summary>
    /// A rule that did not apply says so rather than reporting a pass. A recipe citing no source has not
    /// cleared the attribution rule — there was nothing to check, and stating that is the honest answer.
    /// </summary>
    [Fact]
    public async Task An_inapplicable_rule_is_distinguished_from_a_satisfied_one()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var finding = await FindingFor(
            client, _fixture.WorkspaceA, recipeId, RecipeReadinessCatalogue.AttributionPresent);

        Assert.Equal(nameof(RecipeReadinessStatus.NotApplicable), finding.GetProperty("status").GetString());

        // Nothing to explain and nothing to point at: the rule's own summary already says everything.
        Assert.Equal(JsonValueKind.Null, finding.GetProperty("detail").ValueKind);
        Assert.Empty(finding.GetProperty("evidence").EnumerateArray());
    }

    /// <summary>The counts agree with the findings, so a screen reading both cannot disagree with itself.</summary>
    [Fact]
    public async Task The_counts_agree_with_the_findings()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));

        var findings = body.GetProperty("findings").EnumerateArray().ToList();

        Assert.Equal(
            findings.Count(f => f.GetProperty("status").GetString() == nameof(RecipeReadinessStatus.Blocker)),
            body.GetProperty("blockerCount").GetInt32());
        Assert.Equal(
            findings.Count(f => f.GetProperty("status").GetString() == nameof(RecipeReadinessStatus.Recommendation)),
            body.GetProperty("recommendationCount").GetInt32());
    }

    // ---- The moment it describes ----

    /// <summary>
    /// The result names the version its content belongs to and the recipe's own concurrency token, which
    /// together are what let a client tell whether the readiness it is showing still describes the recipe it
    /// is showing.
    /// </summary>
    [Fact]
    public async Task The_result_names_the_version_and_the_token_the_recipe_carries()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var recipe = await ReadAsync(await client.GetAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));
        var readiness = await ReadAsync(await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));

        Assert.Equal(recipeId, readiness.GetProperty("recipeId").GetGuid());
        Assert.Equal(
            recipe.GetProperty("concurrencyToken").GetString(),
            readiness.GetProperty("concurrencyToken").GetString());
        Assert.Equal(
            recipe.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32(),
            readiness.GetProperty("evaluatedVersionNumber").GetInt32());
        Assert.Equal(
            recipe.GetProperty("currentVersion").GetProperty("id").GetGuid(),
            readiness.GetProperty("evaluatedVersionId").GetGuid());
    }

    /// <summary>
    /// The stale-version case, and the reason the version and the token are published at all: an edit moves
    /// both, and the verdict moves with them. A client holding the earlier answer can tell it has gone stale
    /// rather than showing a verdict about content that has since changed.
    /// </summary>
    [Fact]
    public async Task An_edit_moves_the_version_the_token_and_the_verdict()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var before = await ReadAsync(await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));

        Assert.Equal(
            nameof(RecipeReadinessStatus.Blocker),
            StatusOf(before, RecipeReadinessCatalogue.IngredientsPresent));

        await AddIngredientAsync(client, _fixture.WorkspaceA, recipeId, "2 cups flour");

        var after = await ReadAsync(await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));

        // The rule the edit cleared, and only through a fresh evaluation: nothing was stored for it to update.
        Assert.Equal(
            nameof(RecipeReadinessStatus.Satisfied),
            StatusOf(after, RecipeReadinessCatalogue.IngredientsPresent));

        // The version the answer describes moved with it, which is the half of the staleness signal this host
        // can show. The token's half cannot be shown here: these tests run against SQLite, whose customizer
        // never bumps a row version, so both answers quote the same token however much the recipe changed.
        // `RecipeDataLayerTests.Archiving_writes_the_status_and_its_audit_entry_in_one_unit` is where that is
        // asserted, against a real SQL Server that generates one.
        Assert.True(
            after.GetProperty("evaluatedVersionNumber").GetInt32()
                > before.GetProperty("evaluatedVersionNumber").GetInt32());
    }

    /// <summary>
    /// Evaluating writes nothing. Asserted from outside, because "this is a read" is a claim about what the
    /// request leaves behind rather than about the code that serves it: the recipe's audit timestamp and its
    /// version count are what a write would have moved.
    /// </summary>
    /// <remarks>
    /// The token is compared too and carries less weight than it appears to — this host runs on SQLite, whose
    /// customizer never bumps a row version, so it would not have moved even if something had written. The
    /// timestamp and the version number are the assertions doing the work here.
    /// </remarks>
    [Fact]
    public async Task Evaluating_twice_changes_nothing_about_the_recipe()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var before = await ReadAsync(await client.GetAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));

        await client.GetAsync(ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        await client.GetAsync(ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);

        var after = await ReadAsync(await client.GetAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));

        Assert.Equal(
            before.GetProperty("concurrencyToken").GetString(),
            after.GetProperty("concurrencyToken").GetString());
        Assert.Equal(
            before.GetProperty("updatedAt").GetString(),
            after.GetProperty("updatedAt").GetString());
        Assert.Equal(
            before.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32(),
            after.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// The answer describes one moment, so nothing may serve it again as though it were current — including a
    /// shared proxy, which gateway.md forbids caching personalized responses at.
    /// </summary>
    [Fact]
    public async Task The_answer_is_not_cacheable()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    // ---- Authorization ----

    /// <summary>
    /// Every member may read it, a Viewer included. Every fact it surfaces is already readable through the
    /// test history, a proposal or the ingredient tools, so the bar is the one those routes set.
    /// </summary>
    [Fact]
    public async Task A_viewer_may_read_it()
    {
        // Workspace B's member is the Viewer; A's is an Editor.
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await SeedBareAsync(owner, _fixture.WorkspaceB);

        using var viewer = await SignInAsync(_fixture.WorkspaceB, asOwner: false);
        var response = await viewer.GetAsync(
            ReadinessIn(_fixture.WorkspaceB, recipeId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <inheritdoc cref="A_viewer_may_read_it"/>
    [Fact]
    public async Task An_editor_may_read_it()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedBareAsync(owner, _fixture.WorkspaceA);

        using var editor = await SignInAsync(_fixture.WorkspaceA, asOwner: false);
        var response = await editor.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// No session, no answer — and refused before the recipe is looked at, so the status cannot depend on
    /// whether that id exists. A raw client rather than a <c>GatewayClient</c>, which exists to carry a
    /// session.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_refused()
    {
        using var client = _fixture.Gateway.CreateClient(GatewayTestHost.HttpsClient);

        var response = await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected the edge to refuse an anonymous read, got {response.StatusCode}");
    }

    // ---- Not found, and isolation ----

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.RecipeNotFound,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// The other workspace's recipe is answered exactly as an unknown one is — same status, same code — so the
    /// route cannot be used to learn that a neighbour's recipe id exists (tenancy.md).
    /// </summary>
    [Fact]
    public async Task Another_workspaces_recipe_is_indistinguishable_from_an_unknown_one()
    {
        using var inB = await SignInAsync(_fixture.WorkspaceB);
        var theirs = await SeedBareAsync(inB, _fixture.WorkspaceB);

        using var inA = await SignInAsync(_fixture.WorkspaceA);

        // Through A's own slug, which is the only route A's session can use.
        var throughA = await inA.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, theirs), TestContext.Current.CancellationToken);
        var unknown = await inA.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, throughA.StatusCode);
        Assert.Equal(unknown.StatusCode, throughA.StatusCode);
        Assert.Equal(
            (await ReadAsync(unknown)).GetProperty("code").GetString(),
            (await ReadAsync(throughA)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A member of one workspace naming the other's slug is refused at the edge of the tenancy, before the
    /// recipe is looked at — so the answer cannot depend on whether that recipe exists.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_read_through_the_others_slug()
    {
        using var inB = await SignInAsync(_fixture.WorkspaceB);
        var theirs = await SeedBareAsync(inB, _fixture.WorkspaceB);

        using var inA = await SignInAsync(_fixture.WorkspaceA);
        var response = await inA.GetAsync(
            ReadinessIn(_fixture.WorkspaceB, theirs), TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"expected A's session to be refused B's slug, got {response.StatusCode}");
    }

    /// <summary>
    /// Two recipes with the same content in two workspaces evaluate independently, and neither evaluation
    /// names the other's records. The evaluator never sees a workspace, so this is the only place that can be
    /// shown.
    /// </summary>
    [Fact]
    public async Task Each_workspace_evaluates_its_own_recipe_and_names_only_its_own_records()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA);
        using var inB = await SignInAsync(_fixture.WorkspaceB);

        var mine = await SeedBareAsync(inA, _fixture.WorkspaceA);
        var theirs = await SeedBareAsync(inB, _fixture.WorkspaceB);

        await AddIngredientAsync(inA, _fixture.WorkspaceA, mine, "mine: a handful of nothing catalogued");
        await AddIngredientAsync(inB, _fixture.WorkspaceB, theirs, "theirs: a handful of nothing catalogued");

        var forA = await ReadAsync(await inA.GetAsync(
            ReadinessIn(_fixture.WorkspaceA, mine), TestContext.Current.CancellationToken));
        var forB = await ReadAsync(await inB.GetAsync(
            ReadinessIn(_fixture.WorkspaceB, theirs), TestContext.Current.CancellationToken));

        Assert.Equal(mine, forA.GetProperty("recipeId").GetGuid());
        Assert.Equal(theirs, forB.GetProperty("recipeId").GetGuid());

        // The creators' own words, each in its own answer and neither in the other's. Asserted on the raw
        // JSON as well, so a leak nested inside some future field would still be caught.
        Assert.Contains("mine:", forA.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("theirs:", forA.GetRawText(), StringComparison.Ordinal);

        Assert.Contains("theirs:", forB.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("mine:", forB.GetRawText(), StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>A recipe with a title and nothing else, so most rules have something to report.</summary>
    private static async Task<Guid> SeedBareAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Olive oil cake" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await ReadAsync(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>Adds one ingredient line in the creator's own words, in the untitled group lines travel in.</summary>
    private static async Task AddIngredientAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, string displayText)
    {
        var current = await ReadAsync(await client.GetAsync(
            RecipeIn(workspace, recipeId), TestContext.Current.CancellationToken));

        var response = await client.PatchAsJsonAsync(
            RecipeIn(workspace, recipeId),
            new
            {
                expectedConcurrencyToken = current.GetProperty("concurrencyToken").GetString(),
                ingredientGroups = new[]
                {
                    new { ingredients = new[] { new { displayText } } },
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> FindingFor(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, string ruleId)
    {
        var body = await ReadAsync(await client.GetAsync(
            ReadinessIn(workspace, recipeId), TestContext.Current.CancellationToken));

        return body.GetProperty("findings").EnumerateArray()
            .Single(finding => finding.GetProperty("ruleId").GetString() == ruleId);
    }

    private static string? StatusOf(JsonElement body, string ruleId) =>
        body.GetProperty("findings").EnumerateArray()
            .Single(finding => finding.GetProperty("ruleId").GetString() == ruleId)
            .GetProperty("status")
            .GetString();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
