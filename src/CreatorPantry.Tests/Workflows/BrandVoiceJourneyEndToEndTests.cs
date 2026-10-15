using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// "Create my brand voice" end to end through the real Gateway (13A.17), in the order the guided screens and the
/// Phase 11A pages they link to send it: the setup is saved part-way and found again, finished with a guide kept,
/// the guide is named to the run, the optional steps are done or passed over, an Editor approves it but cannot switch
/// it on, and only the Owner's switch-on finishes the journey.
/// </summary>
/// <remarks>
/// The setup's own session, the guide's routes and the activation are Phase 11A's, used as its pages use them. The
/// workflow is only ever asked whether a step is done; it never builds, approves or switches on a guide.
/// </remarks>
public sealed class BrandVoiceJourneyEndToEndTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkspaceIn => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}";

    private string RunsIn => $"{WorkspaceIn}/workflow-runs";

    private string GuidesIn => $"{WorkspaceIn}/brand-style-guides";

    private string SessionIn => $"{WorkspaceIn}/brand-setup-session";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task The_setup_is_carried_to_a_guide_that_only_the_owner_switches_on()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var started = await owner.PostAsJsonAsync(RunsIn, new { definitionKey = "brand.voice" }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        Assert.Equal("set-up", NextStep(await ReadRunAsync(owner, runId)));

        // ---- The setup: none yet, then saved part-way and found again, as the step reads it ----
        Assert.Equal(HttpStatusCode.NoContent, (await owner.GetAsync(SessionIn, Ct)).StatusCode);
        var partWay = await SaveSessionAsync(owner, "examples", ["goals", "style"], "{}", ifMatch: null);
        var reread = await BodyOf(await owner.GetAsync(SessionIn, Ct));
        Assert.Equal("examples", reread.GetProperty("currentStep").GetString());

        // Nothing kept yet, so nothing to name and the step is not done.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "set-up")).StatusCode);

        // ---- The setup's last step keeps the guide as a draft, and the setup is finished ----
        var guideId = await CreateGuideAsync(owner);
        var finish = JsonSerializer.Serialize(new { finish = new { name = "Sam's kitchen voice", guideId, versionNumber = 1 } });
        var atFinish = await SaveSessionAsync(
            owner, "finish", ["goals", "style", "examples", "review-text", "create", "edit"], finish, partWay.GetProperty("rowVersion").GetString());
        var completed = await owner.SendAsync(HttpMethod.Post, $"{SessionIn}/complete", null, IfMatch(atFinish.GetProperty("rowVersion").GetString()), Ct);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        // ---- The step names the guide the setup kept; the server checks it ----
        var afterSetUp = await CompleteAsync(owner, runId, "set-up", [new { kind = "BrandStyleGuide", id = guideId }]);
        Assert.Equal(HttpStatusCode.OK, afterSetUp.StatusCode);
        Assert.Equal("examples", NextStep(await BodyOf(afterSetUp)));

        // ---- Examples done on the creator's word; reading and trying passed over ----
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "examples")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SkipAsync(owner, runId, "review")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SkipAsync(owner, runId, "try")).StatusCode);
        Assert.Equal("switch-on", NextStep(await ReadRunAsync(owner, runId)));

        // ---- Switch on: not done until the guide is the one switched on ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "switch-on")).StatusCode);

        // An Editor may approve it, but switching on is the Owner's alone — and nothing in the workflow moves it.
        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: Ct);
        var approved = await editor.PostAsJsonAsync(
            $"{GuidesIn}/{guideId}/versions/1/approval", new { confirmed = true, reason = "Reads like Sam." }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var refused = await editor.PostAsJsonAsync(
            $"{GuidesIn}/{guideId}/versions/1/activation", new { confirmed = true, reason = "Go." }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "switch-on")).StatusCode);

        var switchedOn = await owner.PostAsJsonAsync(
            $"{GuidesIn}/{guideId}/versions/1/activation", new { confirmed = true, reason = "Our voice from today." }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.OK, switchedOn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "switch-on")).StatusCode);

        // ---- Finish ----
        var finished = await owner.PostAsJsonAsync(
            $"{RunsIn}/{runId}/completion", new { expectedConcurrencyToken = await TokenAsync(owner, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal("Completed", (await BodyOf(finished)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_setup_that_switched_the_voice_on_itself_leaves_the_last_step_already_done()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var started = await owner.PostAsJsonAsync(RunsIn, new { definitionKey = "brand.voice" }, Guid.NewGuid().ToString(), Ct);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();

        // The setup's "Switch my voice on": approve, then switch on — its own two writes, as the Owner.
        var guideId = await CreateGuideAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(
            $"{GuidesIn}/{guideId}/versions/1/approval", new { confirmed = true, reason = (string?)null }, Guid.NewGuid().ToString("N"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(
            $"{GuidesIn}/{guideId}/versions/1/activation", new { confirmed = true, reason = (string?)null }, Guid.NewGuid().ToString("N"), Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "set-up", [new { kind = "BrandStyleGuide", id = guideId }])).StatusCode);
        foreach (var optional in new[] { "examples", "review", "try" })
        {
            Assert.Equal(HttpStatusCode.OK, (await SkipAsync(owner, runId, optional)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "switch-on")).StatusCode);
        Assert.True((await ReadRunAsync(owner, runId)).GetProperty("readyToComplete").GetBoolean());
    }

    [Fact]
    public async Task Switching_a_new_guide_on_over_another_must_name_the_voice_it_replaces()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        // Nothing on yet: the current-voice read says so, and does not fall back to some approved version.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.GetAsync($"{GuidesIn}/active", Ct)).StatusCode);

        var first = await CreateGuideAsync(owner);
        await ApproveAsync(owner, first);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, first, expectedActiveVersionId: null)).StatusCode);

        var active = await owner.GetAsync($"{GuidesIn}/active", Ct);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var voice = await BodyOf(active);
        Assert.Equal(first, voice.GetProperty("guideId").GetGuid());
        var firstVersionId = voice.GetProperty("version").GetProperty("id").GetGuid();

        // A second guide, as a second run of the journey builds one: claiming nothing is on is refused, so a voice
        // the Owner never saw is never replaced; naming the one on now is what the switch-on step sends.
        var second = await CreateGuideAsync(owner);
        await ApproveAsync(owner, second);
        Assert.Equal(HttpStatusCode.Conflict, (await ActivateAsync(owner, second, expectedActiveVersionId: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(owner, second, expectedActiveVersionId: firstVersionId)).StatusCode);
        Assert.Equal(second, (await BodyOf(await owner.GetAsync($"{GuidesIn}/active", Ct))).GetProperty("guideId").GetGuid());

        // Another workspace reads only its own voice.
        using var neighbour = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await neighbour.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/brand-style-guides/active", Ct)).StatusCode);
    }

    [Fact]
    public async Task Another_workspace_can_neither_read_the_run_nor_name_its_guide_on_a_run_of_its_own()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var started = await owner.PostAsJsonAsync(RunsIn, new { definitionKey = "brand.voice" }, Guid.NewGuid().ToString(), Ct);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        var guideId = await CreateGuideAsync(owner);

        using var neighbour = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirs = $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}";
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync($"{theirs}/workflow-runs/{runId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync($"{theirs}/brand-style-guides/{guideId}", Ct)).StatusCode);

        // Their own run cannot be completed on this workspace's guide: it is answered as one that does not exist.
        var theirRun = await neighbour.PostAsJsonAsync($"{theirs}/workflow-runs", new { definitionKey = "brand.voice" }, Guid.NewGuid().ToString(), Ct);
        var theirRunId = (await BodyOf(theirRun)).GetProperty("id").GetGuid();
        var token = (await BodyOf(await neighbour.GetAsync($"{theirs}/workflow-runs/{theirRunId}", Ct))).GetProperty("concurrencyToken").GetString();
        var named = await neighbour.PostAsJsonAsync(
            $"{theirs}/workflow-runs/{theirRunId}/steps/set-up/completion",
            new { results = new[] { new { kind = "BrandStyleGuide", id = guideId } }, expectedConcurrencyToken = token },
            Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, named.StatusCode);
    }

    // ---- Helpers ----

    private static string? NextStep(JsonElement run) =>
        run.GetProperty("nextAction").ValueKind == JsonValueKind.Null
            ? null
            : run.GetProperty("nextAction").GetProperty("stepKey").GetString();

    private static Dictionary<string, string>? IfMatch(string? token) =>
        token is null ? null : new Dictionary<string, string> { ["If-Match"] = token };

    private async Task<JsonElement> ReadRunAsync(GatewayClient client, Guid runId) =>
        await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct));

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await ReadRunAsync(client, runId)).GetProperty("concurrencyToken").GetString()!;

    private async Task<HttpResponseMessage> CompleteAsync(GatewayClient client, Guid runId, string stepKey, object[]? results = null) =>
        await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/{stepKey}/completion",
            new { results, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);

    private async Task<HttpResponseMessage> SkipAsync(GatewayClient client, Guid runId, string stepKey) =>
        await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/{stepKey}/skip",
            new { reason = (string?)null, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);

    /// <summary>Saves the setup's session as the wizard's autosave does: its step, its answers, the version it read.</summary>
    private async Task<JsonElement> SaveSessionAsync(GatewayClient client, string current, string[] completed, string draftJson, string? ifMatch)
    {
        var response = await client.SendAsync(
            HttpMethod.Put,
            SessionIn,
            new { currentStep = current, furthestStep = current, completedSteps = completed, skippedSteps = Array.Empty<string>(), draftJson },
            IfMatch(ifMatch is null ? null : $"\"{ifMatch}\""),
            Ct);
        Assert.True(response.IsSuccessStatusCode, $"saving the setup answered {response.StatusCode}");
        return await BodyOf(response);
    }

    private async Task ApproveAsync(GatewayClient client, Guid guideId) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"{GuidesIn}/{guideId}/versions/1/approval", new { confirmed = true, reason = (string?)null }, Guid.NewGuid().ToString("N"), Ct)).StatusCode);

    private Task<HttpResponseMessage> ActivateAsync(GatewayClient client, Guid guideId, Guid? expectedActiveVersionId) =>
        client.PostAsJsonAsync(
            $"{GuidesIn}/{guideId}/versions/1/activation",
            new { confirmed = true, reason = (string?)null, expectedActiveVersionId },
            Guid.NewGuid().ToString("N"),
            Ct);

    /// <summary>The guide the setup's last step keeps, created the way it creates one.</summary>
    private async Task<Guid> CreateGuideAsync(GatewayClient client)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn,
            new { displayName = "Sam's kitchen voice", questionnaire = new { voice = "Warm, direct, never fussy." } },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }
}
