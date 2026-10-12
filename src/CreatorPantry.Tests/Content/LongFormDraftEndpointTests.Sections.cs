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
/// LF.3.3's routes through the gateway: status codes, shapes, roles and isolation. What a section request does
/// once the worker runs it, and the allowance arithmetic, are <c>LongFormOutlineRequestSeamTests</c>'.
/// </summary>
public sealed partial class LongFormDraftEndpointTests
{
    [Fact]
    public async Task The_section_routes_answer_with_the_sections_a_queued_request_and_a_new_revision_for_each_write()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await ApprovedDraftAsync(client, A);
        var sections = $"{DraftsIn(A)}/{Id(draft)}/sections";

        var listed = await client.GetAsync(sections, Ct);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Contains("no-store", listed.Headers.CacheControl!.ToString());

        var before = await BodyOf(listed);
        var plan = before.GetProperty("draft").GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(Article.Select(section => section.Heading), plan.Select(section => section.GetProperty("heading").GetString()));
        Assert.All(plan, section =>
        {
            Assert.False(section.GetProperty("isWritten").GetBoolean());
            Assert.Equal(JsonValueKind.Null, section.GetProperty("body").ValueKind);
        });
        Assert.Empty(before.GetProperty("requests").EnumerateArray());
        Assert.Equal("Outlined", before.GetProperty("draft").GetProperty("status").GetString());

        var first = Id(plan[0]);
        var second = Id(plan[1]);

        // One section: accepted, pointing at the place to read where things stand.
        var asked = await client.PostAsJsonAsync($"{sections}/{first}/writing-requests", new { revisionNumber = 1 }, "write-1", Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.EndsWith($"/long-form-drafts/{Id(draft)}/sections", asked.Headers.Location!.ToString());

        var queued = await BodyOf(asked);
        Assert.Equal(first, queued.GetProperty("request").GetProperty("sectionId").GetGuid());
        Assert.Equal("Requested", queued.GetProperty("request").GetProperty("request").GetProperty("status").GetString());
        Assert.Equal("LongFormSection", queued.GetProperty("request").GetProperty("request").GetProperty("taskType").GetString());
        Assert.Equal("Writing", queued.GetProperty("draft").GetProperty("status").GetString());

        // A retry is the same request; the same key for another section is not a retry of anything.
        var replay = await client.PostAsJsonAsync($"{sections}/{first}/writing-requests", new { revisionNumber = 1 }, "write-1", Ct);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replayed"));

        var reused = await client.PostAsJsonAsync($"{sections}/{second}/writing-requests", new { revisionNumber = 1 }, "write-1", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(reused)));

        var keyless = await client.PostAsJsonAsync($"{sections}/{second}/writing-requests", new { revisionNumber = 1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, keyless.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, Code(await BodyOf(keyless)));

        // The creator's own text for the second section.
        var token = Token(queued.GetProperty("draft"));
        var edited = await client.PatchAsJsonAsync($"{sections}/{second}", new { expectedConcurrencyToken = token, body = "My own words." }, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var afterEdit = await BodyOf(edited);
        Assert.Equal(2, afterEdit.GetProperty("revisionNumber").GetInt32());
        Assert.Equal("My own words.", afterEdit.GetProperty("sections")[1].GetProperty("body").GetString());
        Assert.Equal("CreatorEdit", afterEdit.GetProperty("sections")[1].GetProperty("writtenBy").GetString());

        // Settled, and then not.
        var settled = await client.PostAsJsonAsync(
            $"{sections}/{second}/settlement", new { expectedConcurrencyToken = Token(afterEdit), settled = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
        var afterSettle = await BodyOf(settled);
        Assert.True(afterSettle.GetProperty("sections")[1].GetProperty("settled").GetBoolean());
        Assert.Equal("Writing", afterSettle.GetProperty("status").GetString());

        // The rest: not the one being written, not the one that is settled.
        var rest = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/section-writing-batches", new { revisionNumber = 3 }, "rest-1", Ct);
        Assert.Equal(HttpStatusCode.Accepted, rest.StatusCode);

        var batch = (await BodyOf(rest)).GetProperty("requests").EnumerateArray().ToList();
        Assert.Equal(plan.Skip(2).Select(Id), batch.Select(request => request.GetProperty("sectionId").GetGuid()));

        // Every section that was asked for is waiting, and says so.
        var status = await BodyOf(await client.GetAsync(sections, Ct));
        Assert.Equal(plan.Count - 1, status.GetProperty("requests").GetArrayLength());
        Assert.All(status.GetProperty("requests").EnumerateArray(), request =>
            Assert.Equal("Waiting", request.GetProperty("outcome").GetString()));
    }

    [Theory]
    [InlineData("{}", "revisionNumber")]
    [InlineData("{\"revisionNumber\":0}", "revisionNumber")]
    public async Task A_section_request_that_names_no_revision_is_a_400_naming_the_field(string json, string field)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await ApprovedDraftAsync(client, A);
        var section = Id(draft.GetProperty("latest").GetProperty("sections")[0]);

        var response = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/sections/{section}/writing-requests", JsonSerializer.Deserialize<JsonElement>(json), "write-1", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(AiLongFormSectionRequestErrors.RequestInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_draft_whose_plan_is_not_approved_refuses_every_section_write_with_a_409()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);
        var section = Id(draft.GetProperty("latest").GetProperty("sections")[0]);
        var root = $"{DraftsIn(A)}/{Id(draft)}";

        foreach (var response in new[]
        {
            await client.PostAsJsonAsync($"{root}/sections/{section}/writing-requests", new { revisionNumber = 1 }, "write-1", Ct),
            await client.PostAsJsonAsync($"{root}/section-writing-batches", new { revisionNumber = 1 }, "rest-1", Ct),
            await client.PatchAsJsonAsync($"{root}/sections/{section}", new { expectedConcurrencyToken = Token(draft), body = "Mine." }, Ct),
            await client.PostAsJsonAsync($"{root}/sections/{section}/settlement", new { expectedConcurrencyToken = Token(draft), settled = true }, Ct),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ContentErrorCodes.LongFormPlanNotApproved, Code(await BodyOf(response)));
        }

        // Reading is not writing.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{root}/sections", Ct)).StatusCode);
        Assert.Equal(1, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().AiOperations.CountAsync(Ct)));
    }

    [Fact]
    public async Task While_the_task_is_switched_off_a_section_request_is_refused_and_an_edit_is_not()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await ApprovedDraftAsync(client, A);
        var section = Id(draft.GetProperty("latest").GetProperty("sections")[0]);
        var root = $"{DraftsIn(A)}/{Id(draft)}";
        Tasks.Enabled.Remove(AiTaskCatalog.LongFormSection);

        var asked = await client.PostAsJsonAsync($"{root}/sections/{section}/writing-requests", new { revisionNumber = 1 }, "write-1", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, asked.StatusCode);
        Assert.Equal(AiLongFormSectionRequestErrors.TaskNotEnabled, Code(await BodyOf(asked)));

        // A creator's own text calls no model, so no switch stands in its way.
        var token = Token((await BodyOf(await client.GetAsync($"{root}/sections", Ct))).GetProperty("draft"));
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PatchAsJsonAsync($"{root}/sections/{section}", new { expectedConcurrencyToken = token, body = "Mine." }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_read_the_sections_and_may_not_ask_for_edit_or_settle_one()
    {
        using var owner = await SignInAsync(B.OwnerEmail);
        using var viewer = await SignInAsync(B.MemberEmail);
        var draft = await ApprovedDraftAsync(owner, B);
        var section = Id(draft.GetProperty("latest").GetProperty("sections")[0]);
        var root = $"{DraftsIn(B)}/{Id(draft)}";

        Assert.Equal(WorkspaceRole.Viewer, B.MemberRole);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{root}/sections", Ct)).StatusCode);

        foreach (var response in new[]
        {
            await viewer.PostAsJsonAsync($"{root}/sections/{section}/writing-requests", new { revisionNumber = 1 }, "viewer-1", Ct),
            await viewer.PostAsJsonAsync($"{root}/section-writing-batches", new { revisionNumber = 1 }, "viewer-2", Ct),
            await viewer.PatchAsJsonAsync($"{root}/sections/{section}", new { expectedConcurrencyToken = Token(draft), body = "Viewer's." }, Ct),
            await viewer.PostAsJsonAsync($"{root}/sections/{section}/settlement", new { expectedConcurrencyToken = Token(draft), settled = true }, Ct),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // Nothing a viewer tried took effect: the plan is still open and nothing was queued for a section.
        var after = await BodyOf(await owner.GetAsync($"{root}/sections", Ct));
        Assert.Equal("Outlined", after.GetProperty("draft").GetProperty("status").GetString());
        Assert.Equal(1, after.GetProperty("draft").GetProperty("revisionNumber").GetInt32());
        Assert.Empty(after.GetProperty("requests").EnumerateArray());
    }

    [Fact]
    public async Task One_workspace_cannot_reach_a_section_of_anothers_draft_on_any_section_route()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var mine = await ApprovedDraftAsync(ours, A);
        var draft = await ApprovedDraftAsync(theirs, B);
        var theirSection = Id(draft.GetProperty("latest").GetProperty("sections")[0]);
        var root = $"{DraftsIn(A)}/{Id(draft)}";

        // A, signed in to A, naming B's draft and B's section on A's own routes.
        foreach (var response in new[]
        {
            await ours.GetAsync($"{root}/sections", Ct),
            await ours.PostAsJsonAsync($"{root}/sections/{theirSection}/writing-requests", new { revisionNumber = 1 }, "theirs-1", Ct),
            await ours.PostAsJsonAsync($"{root}/section-writing-batches", new { revisionNumber = 1 }, "theirs-2", Ct),
            await ours.PatchAsJsonAsync($"{root}/sections/{theirSection}", new { expectedConcurrencyToken = Token(draft), body = "Planted." }, Ct),
            await ours.PostAsJsonAsync($"{root}/sections/{theirSection}/settlement", new { expectedConcurrencyToken = Token(draft), settled = true }, Ct),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, Code(await BodyOf(response)));
        }

        // B's section under A's own draft is not a section of that draft.
        var crossed = await ours.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(mine)}/sections/{theirSection}", new { expectedConcurrencyToken = Token(mine), body = "Planted." }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormSectionNotFound, Code(await BodyOf(crossed)));

        // A on B's routes is not a member of B at all: the workspace itself is what is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await ours.GetAsync($"{DraftsIn(B)}/{Id(draft)}/sections", Ct)).StatusCode);

        // B's draft is exactly as B left it, and nothing A sent queued or wrote anything.
        var untouched = await BodyOf(await theirs.GetAsync($"{DraftsIn(B)}/{Id(draft)}/sections", Ct));
        Assert.Equal("Outlined", untouched.GetProperty("draft").GetProperty("status").GetString());
        Assert.All(untouched.GetProperty("draft").GetProperty("sections").EnumerateArray(), section =>
            Assert.False(section.GetProperty("isWritten").GetBoolean()));
        Assert.Equal(1, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().AiOperations.CountAsync(Ct)));
        Assert.Equal(1, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().LongFormRevisions.CountAsync(Ct)));
    }

    /// <summary>A draft whose plan has landed and been approved over HTTP, as it reads afterwards.</summary>
    private async Task<JsonElement> ApprovedDraftAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var draft = await DraftWithPlanAsync(client, workspace);
        var approved = await ApproveAsync(client, workspace, draft, revisionNumber: 1);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        return await BodyOf(approved);
    }
}
