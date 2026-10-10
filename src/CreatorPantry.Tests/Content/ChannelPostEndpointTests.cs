using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// AF.6.4 over HTTP, through the real gateway, for two workspaces that share a name and nothing else: the
/// request and status routes, the creator's edit, and the per-channel decision.
/// </summary>
/// <remarks>
/// <para>
/// The first words on a channel are written through the edit route rather than by generating, so these tests
/// need no provider and no worker: what is under test here is the contract — routes, statuses, stable error
/// codes and what a neighbouring workspace can reach — and <see cref="Ai.ChannelPostRequestSeamTests"/> covers
/// what a generation does once it lands.
/// </para>
/// <para>
/// The channel-posts task is enabled on the host, because a request route whose task is switched off answers
/// the same way for every test and would prove nothing about the rest of the seam. One test turns it back off.
/// </para>
/// </remarks>
public sealed class ChannelPostEndpointTests : IAsyncLifetime
{
    private const string Caption = "Olive oil cake, still warm. Recipe on the blog.";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();
        Tasks.Enabled.Add(AiTaskCatalog.ChannelPosts);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    /// <summary>The host's own options object, so a test can switch the task on or off as a deployment would.</summary>
    private AiTaskOptions Tasks => _fixture.Api.Factory.Services.GetRequiredService<AiTaskOptions>();

    private static string PostsIn(SeededWorkspace workspace, Guid contextId) =>
        $"/api/v1/workspaces/{workspace.Slug}/creative-contexts/{contextId}/posts";

    private static string RequestsIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/channel-post-requests";

    // ---- the request route -------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_is_accepted_with_an_operation_to_poll()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.PostAsJsonAsync(
            RequestsIn(A), Asking(contextId, "instagram", "pinterest"), "key-1", Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await BodyOf(response);
        var requestId = body.GetProperty("aiProposalRequestId").GetGuid();
        Assert.Equal("Requested", body.GetProperty("status").GetString());
        Assert.Equal("ChannelPosts", body.GetProperty("taskType").GetString());
        Assert.Equal($"{RequestsIn(A)}/{requestId}", response.Headers.Location!.ToString());

        // The status resource exists from the moment the request was accepted, and reads as a status rather
        // than as an absence.
        var status = await client.GetAsync($"{RequestsIn(A)}/{requestId}", Ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        var polled = await BodyOf(status);
        Assert.Equal("Requested", polled.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, polled.GetProperty("package").ValueKind);
    }

    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.PostAsJsonAsync(RequestsIn(A), Asking(contextId, "instagram"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task Replaying_the_same_request_returns_the_first_operation()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var asking = Asking(contextId, "instagram");

        var first = await client.PostAsJsonAsync(RequestsIn(A), asking, "key-1", Ct);
        var again = await client.PostAsJsonAsync(RequestsIn(A), asking, "key-1", Ct);

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal(
            (await BodyOf(first)).GetProperty("aiProposalRequestId").GetGuid(),
            (await BodyOf(again)).GetProperty("aiProposalRequestId").GetGuid());
    }

    [Fact]
    public async Task The_same_key_naming_different_channels_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        await client.PostAsJsonAsync(RequestsIn(A), Asking(contextId, "instagram"), "key-1", Ct);
        var different = await client.PostAsJsonAsync(
            RequestsIn(A), Asking(contextId, "instagram", "pinterest"), "key-1", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(different)));
    }

    [Fact]
    public async Task A_request_for_a_piece_of_work_in_the_other_workspace_is_not_found()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var theirContext = await ContextAsync(theirs, B);

        var response = await ours.PostAsJsonAsync(
            RequestsIn(A), Asking(theirContext, "instagram"), "key-1", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(AiChannelPostsRequestErrors.ContextNotFound, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_disabled_task_refuses_the_request_route()
    {
        Tasks.Enabled.Remove(AiTaskCatalog.ChannelPosts);
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.PostAsJsonAsync(RequestsIn(A), Asking(contextId, "instagram"), "key-1", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AiChannelPostsRequestErrors.TaskNotEnabled, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task Another_workspaces_request_is_not_found_on_the_status_route()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var theirContext = await ContextAsync(theirs, B);
        var queued = await theirs.PostAsJsonAsync(
            RequestsIn(B), Asking(theirContext, "instagram"), "key-1", Ct);
        var requestId = (await BodyOf(queued)).GetProperty("aiProposalRequestId").GetGuid();

        var fromA = await ours.GetAsync($"{RequestsIn(A)}/{requestId}", Ct);
        var fromAUnderTheirSlug = await ours.GetAsync($"{RequestsIn(B)}/{requestId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, fromA.StatusCode);
        Assert.Equal(AiChannelPostsRequestErrors.RequestNotFound, Code(await BodyOf(fromA)));

        // Their slug with our session is not their workspace: no membership, and the workspace is not
        // disclosed to exist.
        Assert.Equal(HttpStatusCode.NotFound, fromAUnderTheirSlug.StatusCode);
    }

    // ---- reading the package -----------------------------------------------------------------------------

    [Fact]
    public async Task A_piece_of_work_with_nothing_written_for_it_has_an_empty_package()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.GetAsync(PostsIn(A, contextId), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task A_piece_of_work_in_the_other_workspace_has_no_readable_package()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var theirContext = await ContextAsync(theirs, B);
        await EditAsync(theirs, B, theirContext, "instagram", Caption);

        var response = await ours.GetAsync(PostsIn(A, theirContext), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialNotFound, Code(await BodyOf(response)));
    }

    // ---- the creator's edit ------------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_opens_the_channel_and_the_server_measures_it()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var package = await EditAsync(client, A, contextId, "instagram", Caption);
        var channel = Channel(package, "instagram");

        Assert.Equal("Proposed", channel.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, channel.GetProperty("accepted").ValueKind);
        Assert.Equal(1, channel.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal("CreatorEdit", channel.GetProperty("latest").GetProperty("source").GetString());
        Assert.Equal(Caption, channel.GetProperty("latest").GetProperty("body").GetString());

        // Measured by the channel's profile, not by the client: the request had nowhere to put a count.
        Assert.Equal(Caption.Length, channel.GetProperty("latest").GetProperty("characterCount").GetInt32());
        Assert.Equal(2200, channel.GetProperty("latest").GetProperty("characterLimit").GetInt32());
        Assert.Equal("Within", channel.GetProperty("latest").GetProperty("limitStatus").GetString());
    }

    [Fact]
    public async Task A_second_edit_is_a_new_revision_and_keeps_the_first()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var first = await EditAsync(client, A, contextId, "instagram", Caption);

        var second = await EditAsync(
            client, A, contextId, "instagram", "Second thoughts.", RevisionId(first, "instagram"));

        Assert.Equal(2, Channel(second, "instagram").GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal(2, await CountRevisionsAsync(A));
    }

    [Fact]
    public async Task An_edit_composed_against_an_older_revision_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var first = await EditAsync(client, A, contextId, "instagram", Caption);
        var stale = RevisionId(first, "instagram");
        await EditAsync(client, A, contextId, "instagram", "Second thoughts.", stale);

        var response = await client.PatchAsJsonAsync(
            $"{PostsIn(A, contextId)}/instagram",
            new { body = "Third, against the first.", expectedLatestRevisionId = stale },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialStale, Code(await BodyOf(response)));
        Assert.Equal(2, await CountRevisionsAsync(A));
    }

    [Fact]
    public async Task An_empty_body_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.PatchAsJsonAsync(
            $"{PostsIn(A, contextId)}/instagram", new { body = "   " }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialInvalid, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_channel_posts_are_not_written_for_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.PatchAsJsonAsync(
            $"{PostsIn(A, contextId)}/not-a-channel", new { body = Caption }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialChannelUnprocessable, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_viewer_in_the_other_workspace_cannot_edit_its_posts()
    {
        using var owner = await SignInAsync(B.OwnerEmail);
        using var viewer = await SignInAsync(B.MemberEmail);
        var contextId = await ContextAsync(owner, B);

        var response = await viewer.PatchAsJsonAsync(
            $"{PostsIn(B, contextId)}/instagram", new { body = Caption }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountRevisionsAsync(B));
    }

    [Fact]
    public async Task Editing_a_piece_of_work_in_the_other_workspace_is_not_found()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var theirContext = await ContextAsync(theirs, B);

        var response = await ours.PatchAsJsonAsync(
            $"{PostsIn(A, theirContext)}/instagram", new { body = Caption }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialNotFound, Code(await BodyOf(response)));
        Assert.Equal(0, await CountRevisionsAsync(B));
    }

    // ---- deciding one channel ----------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_takes_the_newest_words()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var drafted = await EditAsync(client, A, contextId, "instagram", Caption);

        var package = await DecideAsync(client, A, contextId, "instagram", "accept", RevisionId(drafted, "instagram"));
        var channel = Channel(package, "instagram");

        Assert.Equal("Accepted", channel.GetProperty("status").GetString());
        Assert.Equal(
            RevisionId(drafted, "instagram"), channel.GetProperty("accepted").GetProperty("id").GetGuid());
        Assert.True(channel.GetProperty("isCurrent").GetBoolean());
    }

    /// <summary>
    /// The restriction in full: a repeat of the same decision is one decision made twice. No second accepted
    /// revision, no second revision at all, and the same answer.
    /// </summary>
    [Fact]
    public async Task Accepting_twice_does_not_create_two_accepted_revisions()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var drafted = await EditAsync(client, A, contextId, "instagram", Caption);
        var revisionId = RevisionId(drafted, "instagram");

        var first = await DecideAsync(client, A, contextId, "instagram", "accept", revisionId);
        var again = await DecideAsync(client, A, contextId, "instagram", "accept", revisionId);

        Assert.Equal(revisionId, Channel(first, "instagram").GetProperty("accepted").GetProperty("id").GetGuid());
        Assert.Equal(revisionId, Channel(again, "instagram").GetProperty("accepted").GetProperty("id").GetGuid());
        Assert.Equal(1, Channel(again, "instagram").GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal(1, await CountRevisionsAsync(A));
    }

    [Fact]
    public async Task Rejecting_the_newest_words_keeps_what_was_accepted_before_them()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var drafted = await EditAsync(client, A, contextId, "instagram", Caption);
        var accepted = RevisionId(drafted, "instagram");
        await DecideAsync(client, A, contextId, "instagram", "accept", accepted);

        var redrafted = await EditAsync(client, A, contextId, "instagram", "Worse, on reflection.", accepted);
        var package = await DecideAsync(
            client, A, contextId, "instagram", "reject", RevisionId(redrafted, "instagram"));
        var channel = Channel(package, "instagram");

        Assert.Equal("Rejected", channel.GetProperty("status").GetString());
        Assert.Equal(accepted, channel.GetProperty("accepted").GetProperty("id").GetGuid());
        Assert.False(channel.GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task A_decision_naming_anything_but_the_newest_revision_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var first = await EditAsync(client, A, contextId, "instagram", Caption);
        var stale = RevisionId(first, "instagram");
        await EditAsync(client, A, contextId, "instagram", "Second thoughts.", stale);

        var response = await DispositionAsync(client, A, contextId, "instagram", "accept", stale);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialStale, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_decision_without_a_revision_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        await EditAsync(client, A, contextId, "instagram", Caption);

        var response = await DispositionAsync(client, A, contextId, "instagram", "accept", revisionId: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialInvalid, Code(await BodyOf(response)));
    }

    [Fact]
    public async Task A_channel_with_nothing_awaiting_that_decision_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var drafted = await EditAsync(client, A, contextId, "instagram", Caption);
        var revisionId = RevisionId(drafted, "instagram");
        await DecideAsync(client, A, contextId, "instagram", "accept", revisionId);

        var response = await DispositionAsync(client, A, contextId, "instagram", "reject", revisionId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialDecisionConflict, Code(await BodyOf(response)));
    }

    // ---- regenerating one channel ------------------------------------------------------------------------

    /// <summary>
    /// Regenerating is a request, not a decision: it answers `202` with an operation to poll, and it names one
    /// channel — so the neighbour's words, revision and status are exactly as they were.
    /// </summary>
    [Fact]
    public async Task Regenerating_one_channel_queues_a_request_and_leaves_the_others_untouched()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        await EditAsync(client, A, contextId, "instagram", Caption);
        var before = await EditAsync(client, A, contextId, "pinterest", "Soda bread, torn open on linen.");
        var pinterestBefore = Channel(before, "pinterest");

        var response = await DispositionAsync(
            client, A, contextId, "instagram", "regenerate", revisionId: null, idempotencyKey: "key-1");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var queued = await BodyOf(response);
        Assert.Equal("ChannelPosts", queued.GetProperty("taskType").GetString());
        Assert.Equal(
            $"{RequestsIn(A)}/{queued.GetProperty("aiProposalRequestId").GetGuid()}",
            response.Headers.Location!.ToString());

        // The queued request names the one channel and nothing else.
        Assert.Equal(new[] { "instagram" }, await RequestedChannelsAsync(A));

        var after = Channel(await PackageAsync(client, A, contextId), "pinterest");
        Assert.Equal(
            pinterestBefore.GetProperty("latest").GetProperty("id").GetGuid(),
            after.GetProperty("latest").GetProperty("id").GetGuid());
        Assert.Equal(pinterestBefore.GetProperty("status").GetString(), after.GetProperty("status").GetString());
        Assert.Equal(2, await CountRevisionsAsync(A));
    }

    [Fact]
    public async Task Regenerating_without_an_idempotency_key_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        await EditAsync(client, A, contextId, "instagram", Caption);

        var response = await DispositionAsync(client, A, contextId, "instagram", "regenerate", revisionId: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, Code(await BodyOf(response)));
    }

    /// <summary>
    /// Regenerating is the one decision that reaches the AI request seam, so it crosses a module boundary as
    /// well as this route's — and a neighbour's piece of work is still not found, with nothing queued for it.
    /// </summary>
    [Fact]
    public async Task Regenerating_a_piece_of_work_in_the_other_workspace_is_not_found()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var theirContext = await ContextAsync(theirs, B);
        await EditAsync(theirs, B, theirContext, "instagram", Caption);

        var response = await DispositionAsync(
            ours, A, theirContext, "instagram", "regenerate", revisionId: null, idempotencyKey: "key-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(AiChannelPostsRequestErrors.ContextNotFound, Code(await BodyOf(response)));
        Assert.Empty(await RequestedChannelsAsync(A));
        Assert.Empty(await RequestedChannelsAsync(B));
    }

    [Fact]
    public async Task Deciding_about_a_piece_of_work_in_the_other_workspace_is_not_found()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var theirContext = await ContextAsync(theirs, B);
        var drafted = await EditAsync(theirs, B, theirContext, "instagram", Caption);

        var response = await DispositionAsync(
            ours, A, theirContext, "instagram", "accept", RevisionId(drafted, "instagram"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ContentErrorCodes.SocialNotFound, Code(await BodyOf(response)));
        Assert.Equal("Proposed", Channel(await PackageAsync(theirs, B, theirContext), "instagram")
            .GetProperty("status").GetString());
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static object Asking(Guid contextId, params string[] channelKeys) =>
        new { creativeContextId = contextId, channelKeys };

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static JsonElement Channel(JsonElement package, string key) =>
        package.GetProperty("channels").EnumerateArray()
            .Single(channel => channel.GetProperty("channelKey").GetString() == key);

    private static Guid RevisionId(JsonElement package, string key) =>
        Channel(package, key).GetProperty("latest").GetProperty("id").GetGuid();

    private static async Task<Guid> ContextAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/creative-contexts",
            new { workingTitle = "Soda bread, autumn", channelKeys = new[] { "blog", "instagram" } },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> EditAsync(
        GatewayClient client,
        SeededWorkspace workspace,
        Guid contextId,
        string channelKey,
        string body,
        Guid? expectedLatestRevisionId = null)
    {
        var response = await client.PatchAsJsonAsync(
            $"{PostsIn(workspace, contextId)}/{channelKey}",
            new { body, expectedLatestRevisionId },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private static Task<HttpResponseMessage> DispositionAsync(
        GatewayClient client,
        SeededWorkspace workspace,
        Guid contextId,
        string channelKey,
        string decision,
        Guid? revisionId,
        string? idempotencyKey = null)
    {
        var path = $"{PostsIn(workspace, contextId)}/{channelKey}/disposition";
        var body = new { decision, revisionId };

        return idempotencyKey is null
            ? client.PostAsJsonAsync(path, body, Ct)
            : client.PostAsJsonAsync(path, body, idempotencyKey, Ct);
    }

    private static async Task<JsonElement> DecideAsync(
        GatewayClient client,
        SeededWorkspace workspace,
        Guid contextId,
        string channelKey,
        string decision,
        Guid? revisionId)
    {
        var response = await DispositionAsync(client, workspace, contextId, channelKey, decision, revisionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private static async Task<JsonElement> PackageAsync(
        GatewayClient client, SeededWorkspace workspace, Guid contextId)
    {
        var response = await client.GetAsync(PostsIn(workspace, contextId), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private Task<int> CountRevisionsAsync(SeededWorkspace workspace) =>
        InAsync(workspace, db => db.SocialRevisions.CountAsync(Ct));

    /// <summary>The channels every queued post request in this workspace named, in the order it named them.</summary>
    private Task<string[]> RequestedChannelsAsync(SeededWorkspace workspace) =>
        InAsync(workspace, async db =>
        {
            var operations = await db.AiOperations
                .Where(operation => operation.TaskType == AiTaskType.ChannelPosts)
                .Select(operation => operation.TaskInputsJson)
                .ToListAsync(Ct);

            return operations
                .SelectMany(json => ChannelPostsInputs.ReadChannelKeys(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(json!)))
                .ToArray();
        });

    private async Task<T> InAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
