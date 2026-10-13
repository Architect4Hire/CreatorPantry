using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// LF.2.2 over HTTP, through the real gateway, for two workspaces that share a name and nothing else: starting
/// a draft, following its outline request, editing and approving its plan, and listing drafts.
/// </summary>
/// <remarks>
/// <para>
/// No provider and no worker run here. A plan is put on a draft the way the worker's landing step puts one
/// there — through the content module's own facade, against a real proposal row for the request the route
/// queued — so what is under test is the contract: routes, statuses, stable error codes, the concurrency token,
/// and what a neighbouring workspace can reach. What a run does once it is claimed, what a spent allowance
/// refuses and how a cancelled run reads are <see cref="Ai.LongFormOutlineRequestSeamTests"/>'s, with the real
/// worker.
/// </para>
/// <para>
/// The outline task is enabled on the host, because a route whose task is switched off answers the same way
/// for every test and would prove nothing about the rest of the seam. One test turns it back off.
/// </para>
/// </remarks>
public sealed partial class LongFormDraftEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();
        Tasks.Enabled.Add(AiTaskCatalog.LongFormOutline);
        Tasks.Enabled.Add(AiTaskCatalog.LongFormSection);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    /// <summary>The host's own options object, so a test can switch the task on or off as a deployment would.</summary>
    private AiTaskOptions Tasks => _fixture.Api.Factory.Services.GetRequiredService<AiTaskOptions>();

    private static string DraftsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/long-form-drafts";

    private static readonly (LongFormSectionRole Role, string Heading, string Intent)[] Article =
    [
        (LongFormSectionRole.Hook, "The loaf for people who do not bake", "Open on how little this bread asks of the cook."),
        (LongFormSectionRole.WhyItWorks, "Why there is no kneading", "Explain what the buttermilk and the soda do together."),
        (LongFormSectionRole.Closing, "Bake it this weekend", "Send the reader off to try it."),
    ];

    // ---- starting a draft --------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_draft_is_accepted_with_the_draft_to_read_and_a_request_to_poll()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.PostAsJsonAsync(
            DraftsIn(A),
            new { creativeContextId = contextId, kind = "NewsletterIssue", targetWords = 450, wantedRoles = new[] { "Greeting" } },
            "key-1",
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        var body = await BodyOf(response);
        var draft = body.GetProperty("draft");
        var draftId = draft.GetProperty("id").GetGuid();
        var requestId = body.GetProperty("request").GetProperty("aiProposalRequestId").GetGuid();

        Assert.Equal($"{DraftsIn(A)}/{draftId}", response.Headers.Location!.ToString());
        Assert.Equal(contextId, draft.GetProperty("creativeContextId").GetGuid());
        Assert.Equal("NewsletterIssue", draft.GetProperty("kind").GetString());
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("latest").ValueKind);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("planCheck").ValueKind);
        Assert.False(string.IsNullOrEmpty(draft.GetProperty("concurrencyToken").GetString()));
        Assert.Equal("Requested", body.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal("LongFormOutline", body.GetProperty("request").GetProperty("taskType").GetString());

        // The draft is readable, and its request reads as a status rather than as an absence, straight away.
        var read = await client.GetAsync($"{DraftsIn(A)}/{draftId}", Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("Draft", (await BodyOf(read)).GetProperty("status").GetString());

        var status = await client.GetAsync($"{DraftsIn(A)}/{draftId}/outline-requests/{requestId}", Ct);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        var polled = await BodyOf(status);
        Assert.Equal("Requested", polled.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(draftId, polled.GetProperty("draft").GetProperty("id").GetGuid());

        // No workspace id anywhere a client could have supplied one, and none comes back.
        Assert.DoesNotContain("workspaceId", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_start_without_an_idempotency_key_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.PostAsJsonAsync(DraftsIn(A), Starting(contextId), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, Code(await BodyOf(response)));
        Assert.Equal(0, await CountDraftsAsync(A));
    }

    [Fact]
    public async Task Replaying_a_start_returns_the_same_draft_and_the_same_key_for_another_request_is_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var first = await client.PostAsJsonAsync(DraftsIn(A), Starting(contextId), "key-1", Ct);
        var again = await client.PostAsJsonAsync(DraftsIn(A), Starting(contextId), "key-1", Ct);

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal(
            (await BodyOf(first)).GetProperty("draft").GetProperty("id").GetGuid(),
            (await BodyOf(again)).GetProperty("draft").GetProperty("id").GetGuid());

        var different = await client.PostAsJsonAsync(
            DraftsIn(A), new { creativeContextId = contextId, kind = "NewsletterIssue" }, "key-1", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(different)));
        Assert.Equal(1, await CountDraftsAsync(A));
    }

    [Fact]
    public async Task A_context_in_the_other_workspace_and_one_that_does_not_exist_get_the_same_422_and_start_nothing()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var theirContext = await ContextAsync(theirs, B);

        var foreign = await ours.PostAsJsonAsync(DraftsIn(A), Starting(theirContext), "key-1", Ct);
        var unknown = await ours.PostAsJsonAsync(DraftsIn(A), Starting(Guid.NewGuid()), "key-2", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var (foreignBody, unknownBody) = (await BodyOf(foreign), await BodyOf(unknown));
        Assert.Equal(ContentErrorCodes.LongFormContextUnprocessable, Code(foreignBody));
        Assert.Equal(Code(foreignBody), Code(unknownBody));

        // Word for word the same, so the answer cannot be used to ask whether a neighbour's context exists.
        Assert.Equal(Words(foreignBody), Words(unknownBody));
        Assert.NotEmpty(Words(foreignBody));

        Assert.Equal(0, await CountDraftsAsync(A));
        Assert.Equal(0, await CountDraftsAsync(B));
        Assert.Equal(0, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().AiOperations.CountAsync(Ct)));
    }

    [Theory]
    [InlineData("""{ "creativeContextId": "CONTEXT" }""", "kind")]
    [InlineData("""{ "creativeContextId": "CONTEXT", "kind": "BlogArticle", "targetWords": 149 }""", "targetWords")]
    [InlineData("""{ "creativeContextId": "CONTEXT", "kind": "BlogArticle", "targetWords": 3001 }""", "targetWords")]
    [InlineData("""{ "creativeContextId": "CONTEXT", "kind": "BlogArticle", "wantedRoles": ["Greeting"] }""", "wantedRoles")]
    [InlineData("""{ "creativeContextId": "CONTEXT", "kind": "BlogArticle", "wantedRoles": ["Hook", "Hook"] }""", "wantedRoles")]
    public async Task A_start_that_is_not_a_request_for_any_plan_is_a_400_naming_the_field(string json, string field)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);

        var response = await client.PostAsJsonAsync(
            DraftsIn(A), JsonSerializer.Deserialize<JsonElement>(json.Replace("CONTEXT", contextId.ToString())), "key-1", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(AiLongFormOutlineRequestErrors.RequestInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), body.GetRawText());
        Assert.Equal(0, await CountDraftsAsync(A));
    }

    [Fact]
    public async Task A_start_while_the_task_is_switched_off_is_refused_and_starts_nothing()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        Tasks.Enabled.Remove(AiTaskCatalog.LongFormOutline);

        var response = await client.PostAsJsonAsync(DraftsIn(A), Starting(contextId), "key-1", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AiLongFormOutlineRequestErrors.TaskNotEnabled, Code(await BodyOf(response)));
        Assert.Equal(0, await CountDraftsAsync(A));
    }

    // ---- editing the plan --------------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_renames_reorders_removes_and_adds_sections_as_a_new_revision()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);
        var sections = draft.GetProperty("latest").GetProperty("sections").EnumerateArray().ToList();

        var response = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new
            {
                expectedConcurrencyToken = Token(draft),
                sections = new object[]
                {
                    // The closing moved to the front and renamed; the middle section removed; a new one added.
                    new { id = sections[2].GetProperty("id").GetGuid(), role = "Closing", heading = "Go and bake", intent = "Send the reader off." },
                    new { id = sections[0].GetProperty("id").GetGuid(), role = "Hook", heading = sections[0].GetProperty("heading").GetString(), intent = sections[0].GetProperty("intent").GetString() },
                    new { role = "Faq", heading = "Questions you might have", intent = "Answer what a first-time baker asks." },
                },
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var edited = await BodyOf(response);
        var latest = edited.GetProperty("latest");
        Assert.Equal(2, latest.GetProperty("revisionNumber").GetInt32());
        Assert.Equal("CreatorEdit", latest.GetProperty("source").GetString());
        Assert.Equal(
            ["Go and bake", "The loaf for people who do not bake", "Questions you might have"],
            latest.GetProperty("sections").EnumerateArray().Select(section => section.GetProperty("heading").GetString()));

        // A section that was kept keeps its id; one that was added gets one of its own.
        var ids = latest.GetProperty("sections").EnumerateArray().Select(section => section.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(sections[2].GetProperty("id").GetGuid(), ids[0]);
        Assert.Equal(sections[0].GetProperty("id").GetGuid(), ids[1]);
        Assert.DoesNotContain(ids[2], sections.Select(section => section.GetProperty("id").GetGuid()));

        Assert.Equal("Draft", edited.GetProperty("status").GetString());
        Assert.True(edited.GetProperty("planCheck").GetProperty("isValid").GetBoolean());

        // The plan before this one is still the revision it was, and the audit entry says what happened
        // without a word of the plan.
        await InAsync(A, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            var first = await db.LongFormRevisions.AsNoTracking().SingleAsync(revision => revision.RevisionNumber == 1, Ct);
            Assert.Contains("Why there is no kneading", first.Content, StringComparison.Ordinal);

            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(
                entry => entry.Action == ContentAuditActions.LongFormPlanEdited, Ct);
            Assert.Equal(Id(draft).ToString("D"), audit.ResourceId);
            Assert.DoesNotContain("bake", audit.Summary, StringComparison.OrdinalIgnoreCase);

            return 0;
        });
    }

    [Fact]
    public async Task A_plan_may_be_saved_before_it_is_valid_and_the_draft_says_what_stands_in_the_way()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);

        // Two hooks and a blank heading: a creator part-way through rearranging.
        var response = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new
            {
                expectedConcurrencyToken = Token(draft),
                sections = new object[]
                {
                    new { role = "Hook", heading = "One way in", intent = "Open one way." },
                    new { role = "Hook", heading = "", intent = "Open another way." },
                },
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saved = await BodyOf(response);
        var check = saved.GetProperty("planCheck");
        var second = saved.GetProperty("latest").GetProperty("sections")[1].GetProperty("id").GetGuid();

        Assert.False(check.GetProperty("isValid").GetBoolean());
        Assert.Equal(LongFormPolicy.Version, check.GetProperty("policyVersion").GetString());
        Assert.Equal(
            new[] { LongFormFindingCodes.RoleRepeated, LongFormFindingCodes.HeadingBlank }.Order(),
            check.GetProperty("findings").EnumerateArray().Select(finding => finding.GetProperty("code").GetString()).Order());
        Assert.All(
            check.GetProperty("findings").EnumerateArray(),
            finding => Assert.Equal(second, finding.GetProperty("sectionId").GetGuid()));

        // And approving it is refused, with the failing section named by its position.
        var approval = await ApproveAsync(client, A, saved, revisionNumber: 2);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, approval.StatusCode);

        var refused = await BodyOf(approval);
        Assert.Equal(ContentErrorCodes.LongFormPlanUnprocessable, Code(refused));
        Assert.Equal(2, refused.GetProperty("errors").GetProperty("sections[1]").GetArrayLength());
        Assert.False(refused.GetProperty("errors").TryGetProperty("sections[0]", out _));
        Assert.Equal("Draft", (await ReadAsync(client, A, Id(draft))).GetProperty("status").GetString());
    }

    /// <summary>
    /// The contract for a token that is not the draft's. That a real write moves the token, and that a second
    /// writer is therefore refused, needs a store whose row version moves: <c>LongFormDraftTests</c>, on SQL
    /// Server.
    /// </summary>
    [Fact]
    public async Task A_token_that_is_not_the_drafts_is_a_conflict_and_writes_nothing()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);

        var stale = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan", Plan(SomebodyElsesToken, ("Hook", "Second writer", "Open.")), Ct);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormDraftStale, Code(await BodyOf(stale)));

        var staleApproval = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan/approval",
            new { expectedConcurrencyToken = SomebodyElsesToken, revisionNumber = 1 },
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, staleApproval.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormDraftStale, Code(await BodyOf(staleApproval)));

        var now = await ReadAsync(client, A, Id(draft));
        Assert.Equal("Draft", now.GetProperty("status").GetString());
        Assert.Equal(1, now.GetProperty("latest").GetProperty("revisionNumber").GetInt32());

        // A token that is not a token at all is a bad request, not a conflict.
        var malformed = await client.PatchAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/plan", Plan("not-a-token", ("Hook", "x", "y")), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormPlanInvalid, Code(await BodyOf(malformed)));
    }

    [Fact]
    public async Task Saving_the_plan_exactly_as_it_stands_creates_no_revision_and_no_audit_entry()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);

        var response = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new
            {
                expectedConcurrencyToken = Token(draft),
                sections = draft.GetProperty("latest").GetProperty("sections").EnumerateArray().Select(section => new
                {
                    id = section.GetProperty("id").GetGuid(),
                    role = section.GetProperty("role").GetString(),
                    heading = section.GetProperty("heading").GetString(),
                    intent = section.GetProperty("intent").GetString(),
                }),
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var same = await BodyOf(response);
        Assert.Equal(1, same.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal(1, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().LongFormRevisions.CountAsync(Ct)));
        Assert.Equal(0, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .CountAsync(entry => entry.Action == ContentAuditActions.LongFormPlanEdited, Ct)));
    }

    [Fact]
    public async Task A_section_id_from_nowhere_and_a_source_on_the_wrong_section_are_refused()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var pinned = Guid.NewGuid();
        var draft = await DraftWithPlanAsync(client, A, LongFormKind.NewsletterIssue, pinnedReference: pinned);

        async Task<JsonElement> RefusedAsync(object section)
        {
            var response = await client.PatchAsJsonAsync(
                $"{DraftsIn(A)}/{Id(draft)}/plan",
                new { expectedConcurrencyToken = Token(draft), sections = new[] { section } },
                Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await BodyOf(response);
            Assert.Equal(ContentErrorCodes.LongFormPlanInvalid, Code(body));

            return body.GetProperty("errors");
        }

        // An id that is not a section of this plan: it cannot adopt one from anywhere else.
        Assert.True((await RefusedAsync(new { id = Guid.NewGuid(), role = "Greeting", heading = "Hello", intent = "Say hello." }))
            .TryGetProperty("sections[0].id", out _));

        // A source on a section that is not an item, and an item naming a recipe the plan was not written for.
        Assert.True((await RefusedAsync(new { role = "Greeting", heading = "Hello", intent = "Say hello.", sourceReferenceId = pinned }))
            .TryGetProperty("sections[0].sourceReferenceId", out _));
        Assert.True((await RefusedAsync(new { role = "Item", heading = "A loaf", intent = "Introduce it.", sourceReferenceId = Guid.NewGuid() }))
            .TryGetProperty("sections[0].sourceReferenceId", out _));

        // A role that is not a role never reaches the seam.
        var unknownRole = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new { expectedConcurrencyToken = Token(draft), sections = new[] { new { role = "Backstory", heading = "x", intent = "y" } } },
            Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknownRole.StatusCode);

        // An item naming the recipe the plan was written for is kept.
        var kept = await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan",
            new { expectedConcurrencyToken = Token(draft), sections = new[] { new { role = "Item", heading = "A loaf", intent = "Introduce it.", sourceReferenceId = pinned } } },
            Ct);

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(pinned, (await BodyOf(kept)).GetProperty("latest").GetProperty("sections")[0].GetProperty("sourceReferenceId").GetGuid());
        Assert.Equal(2, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().LongFormRevisionRecipePins.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_draft_with_no_plan_yet_cannot_be_edited_or_approved()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var started = await StartAsync(client, A);
        var draft = started.GetProperty("draft");

        var edit = await client.PatchAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/plan", Plan(Token(draft), ("Hook", "Mine", "Open.")), Ct);
        var approval = await ApproveAsync(client, A, draft, revisionNumber: 1);

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormPlanNotStarted, Code(await BodyOf(edit)));
        Assert.Equal(HttpStatusCode.Conflict, approval.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormPlanNotStarted, Code(await BodyOf(approval)));
    }

    // ---- approving the plan ------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_moves_the_draft_to_outlined_and_approving_twice_is_one_approval()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);

        var first = await ApproveAsync(client, A, draft, revisionNumber: 1);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var approved = await BodyOf(first);
        Assert.Equal("Outlined", approved.GetProperty("status").GetString());
        Assert.Equal(1, approved.GetProperty("latest").GetProperty("revisionNumber").GetInt32());

        // The retry a client sends after a lost response, holding a token the first approval has since moved
        // on from. It is the same approval — not a stale write, and not a second one.
        var again = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan/approval",
            new { expectedConcurrencyToken = SomebodyElsesToken, revisionNumber = 1 },
            Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Outlined", (await BodyOf(again)).GetProperty("status").GetString());

        await InAsync(A, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(
                entry => entry.Action == ContentAuditActions.LongFormPlanApproved, Ct);

            Assert.Equal("Draft", audit.BeforeReference);
            Assert.Equal("Outlined", audit.AfterReference);
            Assert.Equal(1, await db.LongFormRevisions.CountAsync(Ct));

            return 0;
        });
    }

    [Fact]
    public async Task An_approval_names_the_revision_it_read_and_an_edit_after_approval_un_approves_the_plan()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);

        var edited = await BodyOf(await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan", Plan(Token(draft), ("Hook", "Rewritten", "Open.")), Ct));

        // The right token, the wrong revision: the plan that was read is not the plan that is there.
        var stale = await ApproveAsync(client, A, edited, revisionNumber: 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormDraftStale, Code(await BodyOf(stale)));

        var approved = await BodyOf(await ApproveAsync(client, A, edited, revisionNumber: 2));
        Assert.Equal("Outlined", approved.GetProperty("status").GetString());

        // Changing an approved plan makes it a plan nobody has approved.
        var changed = await BodyOf(await client.PatchAsJsonAsync(
            $"{DraftsIn(A)}/{Id(draft)}/plan", Plan(Token(approved), ("Hook", "Rewritten again", "Open.")), Ct));

        Assert.Equal("Draft", changed.GetProperty("status").GetString());
        Assert.Equal(3, changed.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
    }

    [Theory]
    [InlineData(LongFormDraftStatus.Writing)]
    [InlineData(LongFormDraftStatus.Assembled)]
    public async Task Once_sections_are_being_written_the_plan_is_closed(LongFormDraftStatus status)
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var draft = await DraftWithPlanAsync(client, A);

        await InAsync(A, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            (await db.LongFormDrafts.SingleAsync(Ct)).Status = status;

            return await db.SaveChangesAsync(Ct);
        });

        var current = await ReadAsync(client, A, Id(draft));

        var edit = await client.PatchAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/plan", Plan(Token(current), ("Hook", "Late", "Open.")), Ct);
        var approval = await ApproveAsync(client, A, current, revisionNumber: 1);
        var again = await client.PostAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/outline-requests", new { }, "key-again", Ct);

        foreach (var response in new[] { edit, approval, again })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ContentErrorCodes.LongFormPlanLocked, Code(await BodyOf(response)));
        }
    }

    // ---- asking again, and listing -----------------------------------------------------------------------

    [Fact]
    public async Task Asking_for_another_outline_queues_a_second_request_for_the_same_draft()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var started = await StartAsync(client, A);
        var draftId = Id(started.GetProperty("draft"));
        var firstRequest = started.GetProperty("request").GetProperty("aiProposalRequestId").GetGuid();

        var response = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{draftId}/outline-requests", new { targetWords = 600, wantedRoles = new[] { "Faq" } }, "key-again", Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await BodyOf(response);
        var secondRequest = body.GetProperty("request").GetProperty("aiProposalRequestId").GetGuid();

        Assert.NotEqual(firstRequest, secondRequest);
        Assert.Equal(draftId, Id(body.GetProperty("draft")));
        Assert.Equal($"{DraftsIn(A)}/{draftId}/outline-requests/{secondRequest}", response.Headers.Location!.ToString());
        Assert.Equal(1, await CountDraftsAsync(A));

        // Both requests read under this draft, and neither reads under a draft it was not made for.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{DraftsIn(A)}/{draftId}/outline-requests/{secondRequest}", Ct)).StatusCode);

        var other = Id((await StartAsync(client, A, "key-other")).GetProperty("draft"));
        var mismatched = await client.GetAsync($"{DraftsIn(A)}/{other}/outline-requests/{secondRequest}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
        Assert.Equal(AiLongFormOutlineRequestErrors.RequestNotFound, Code(await BodyOf(mismatched)));

        // A wanted section this kind does not have is refused on this route as on the first.
        var invalid = await client.PostAsJsonAsync(
            $"{DraftsIn(A)}/{draftId}/outline-requests", new { wantedRoles = new[] { "Greeting" } }, "key-invalid", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task The_list_pages_newest_first_keeps_to_its_filters_and_binds_its_cursor_to_them()
    {
        using var client = await SignInAsync(A.OwnerEmail);
        var contextId = await ContextAsync(client, A);
        var otherContext = await ContextAsync(client, A);

        var ids = new List<Guid>();

        foreach (var (context, kind, key) in new[]
        {
            (contextId, "BlogArticle", "key-1"), (contextId, "NewsletterIssue", "key-2"), (otherContext, "BlogArticle", "key-3"),
        })
        {
            var response = await client.PostAsJsonAsync(DraftsIn(A), new { creativeContextId = context, kind }, key, Ct);
            ids.Add(Id((await BodyOf(response)).GetProperty("draft")));
        }

        var firstPage = await BodyOf(await client.GetAsync($"{DraftsIn(A)}?limit=2", Ct));
        var cursor = firstPage.GetProperty("nextCursor").GetString();

        Assert.Equal(2, firstPage.GetProperty("items").GetArrayLength());
        Assert.NotNull(cursor);

        var secondPage = await BodyOf(await client.GetAsync($"{DraftsIn(A)}?limit=2&cursor={Uri.EscapeDataString(cursor!)}", Ct));
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);

        // Every draft once across the two pages, and nothing of any plan in a row.
        var listed = firstPage.GetProperty("items").EnumerateArray().Concat(secondPage.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal(ids.Order(), listed.Select(Id).Order());
        Assert.All(listed, row => Assert.False(row.TryGetProperty("latest", out _)));

        var articles = await BodyOf(await client.GetAsync($"{DraftsIn(A)}?kind=BlogArticle", Ct));
        Assert.Equal(new[] { ids[0], ids[2] }.Order(), articles.GetProperty("items").EnumerateArray().Select(Id).Order());

        var forContext = await BodyOf(await client.GetAsync($"{DraftsIn(A)}?creativeContextId={otherContext}", Ct));
        Assert.Equal(ids[2], Id(Assert.Single(forContext.GetProperty("items").EnumerateArray())));

        // A cursor issued for the unfiltered list does not page a filtered one.
        var replayed = await client.GetAsync($"{DraftsIn(A)}?kind=BlogArticle&cursor={Uri.EscapeDataString(cursor!)}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormCursorInvalid, Code(await BodyOf(replayed)));

        // Nor does it page another workspace's list: the workspace is part of what the cursor was issued for.
        using var theirs = await SignInAsync(B.OwnerEmail);
        var elsewhere = await theirs.GetAsync($"{DraftsIn(B)}?limit=2&cursor={Uri.EscapeDataString(cursor!)}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Equal(ContentErrorCodes.LongFormCursorInvalid, Code(await BodyOf(elsewhere)));
    }

    // ---- roles, and two workspaces -----------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_may_read_a_draft_and_may_not_start_edit_approve_or_ask_again()
    {
        using var owner = await SignInAsync(B.OwnerEmail);
        using var viewer = await SignInAsync(B.MemberEmail);
        var draft = await DraftWithPlanAsync(owner, B);
        var requestId = await InAsync(B, async services =>
            (await services.GetRequiredService<CreatorPantryDbContext>().AiOperations.AsNoTracking().SingleAsync(Ct)).Id);

        Assert.Equal(WorkspaceRole.Viewer, B.MemberRole);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(DraftsIn(B), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{DraftsIn(B)}/{Id(draft)}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{DraftsIn(B)}/{Id(draft)}/outline-requests/{requestId}", Ct)).StatusCode);

        var contextId = draft.GetProperty("creativeContextId").GetGuid();

        foreach (var response in new[]
        {
            await viewer.PostAsJsonAsync(DraftsIn(B), Starting(contextId), "viewer-key", Ct),
            await viewer.PatchAsJsonAsync($"{DraftsIn(B)}/{Id(draft)}/plan", Plan(Token(draft), ("Hook", "Viewer's", "Open.")), Ct),
            await ApproveAsync(viewer, B, draft, revisionNumber: 1),
            await viewer.PostAsJsonAsync($"{DraftsIn(B)}/{Id(draft)}/outline-requests", new { }, "viewer-again", Ct),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // Nothing a viewer tried took effect.
        var after = await ReadAsync(owner, B, Id(draft));
        Assert.Equal("Draft", after.GetProperty("status").GetString());
        Assert.Equal(1, after.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal(1, await CountDraftsAsync(B));
    }

    [Fact]
    public async Task One_workspace_cannot_read_edit_approve_plan_again_or_poll_anothers_draft_by_its_id()
    {
        using var ours = await SignInAsync(A.OwnerEmail);
        using var theirs = await SignInAsync(B.OwnerEmail);
        var mine = await DraftWithPlanAsync(ours, A);
        var draft = await DraftWithPlanAsync(theirs, B);
        var theirRequest = await InAsync(B, async services =>
            (await services.GetRequiredService<CreatorPantryDbContext>().AiOperations.AsNoTracking().SingleAsync(Ct)).Id);

        // A, signed in to A, naming B's draft by its id on A's own routes.
        var attempts = new (HttpResponseMessage Response, string Code)[]
        {
            (await ours.GetAsync($"{DraftsIn(A)}/{Id(draft)}", Ct), ContentErrorCodes.LongFormDraftNotFound),
            (await ours.PatchAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/plan", Plan(Token(draft), ("Hook", "Planted", "Planted.")), Ct), ContentErrorCodes.LongFormDraftNotFound),
            (await ApproveAsync(ours, A, draft, revisionNumber: 1), ContentErrorCodes.LongFormDraftNotFound),
            (await ours.PostAsJsonAsync($"{DraftsIn(A)}/{Id(draft)}/outline-requests", new { }, "key-theirs", Ct), ContentErrorCodes.LongFormDraftNotFound),
            (await ours.GetAsync($"{DraftsIn(A)}/{Id(draft)}/outline-requests/{theirRequest}", Ct), AiLongFormOutlineRequestErrors.RequestNotFound),

            // And B's request under A's own draft: it is not that draft's, and is not A's to know of.
            (await ours.GetAsync($"{DraftsIn(A)}/{Id(mine)}/outline-requests/{theirRequest}", Ct), AiLongFormOutlineRequestErrors.RequestNotFound),
        };

        foreach (var (response, code) in attempts)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(code, Code(await BodyOf(response)));
        }

        // A on B's routes is not a member of B at all: the workspace itself is what is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await ours.GetAsync(DraftsIn(B), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ours.GetAsync($"{DraftsIn(B)}/{Id(draft)}", Ct)).StatusCode);

        // Each list holds its own draft and not the other's.
        var listedA = (await BodyOf(await ours.GetAsync(DraftsIn(A), Ct))).GetProperty("items").EnumerateArray().Select(Id).ToList();
        var listedB = (await BodyOf(await theirs.GetAsync(DraftsIn(B), Ct))).GetProperty("items").EnumerateArray().Select(Id).ToList();
        Assert.Equal([Id(mine)], listedA);
        Assert.Equal([Id(draft)], listedB);

        // B's draft is exactly as B left it, and nothing A sent wrote a row anywhere.
        var untouched = await ReadAsync(theirs, B, Id(draft));
        Assert.Equal("Draft", untouched.GetProperty("status").GetString());
        Assert.Equal(1, untouched.GetProperty("latest").GetProperty("revisionNumber").GetInt32());
        Assert.Equal(1, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().LongFormRevisions.CountAsync(Ct)));
        Assert.Equal(1, await InAsync(A, services => services.GetRequiredService<CreatorPantryDbContext>().AiOperations.CountAsync(Ct)));
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static object Starting(Guid contextId) => new { creativeContextId = contextId, kind = "BlogArticle" };

    private static object Plan(string token, params (string Role, string Heading, string Intent)[] sections) => new
    {
        expectedConcurrencyToken = token,
        sections = sections.Select(section => new { role = section.Role, heading = section.Heading, intent = section.Intent }),
    };

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();

    /// <summary>Every sentence a problem response says to a person, whichever members carry them.</summary>
    private static string[] Words(JsonElement problem) =>
    [
        .. new[] { "title", "detail" }
            .Where(name => problem.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String)
            .Select(name => problem.GetProperty(name).GetString()!),
    ];

    /// <summary>A well-formed token that is not the draft's: what a client holds after somebody else has written.</summary>
    private const string SomebodyElsesToken = "AAAAAAAAAAE=";

    private static string Token(JsonElement draft) => draft.GetProperty("concurrencyToken").GetString()!;

    private static async Task<Guid> ContextAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/creative-contexts",
            new { workingTitle = "Soda bread, autumn", channelKeys = new[] { "blog" } },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Id(await BodyOf(response));
    }

    private static async Task<JsonElement> StartAsync(GatewayClient client, SeededWorkspace workspace, string key = "key-start", string kind = "BlogArticle")
    {
        var contextId = await ContextAsync(client, workspace);
        var response = await client.PostAsJsonAsync(DraftsIn(workspace), new { creativeContextId = contextId, kind }, key, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return await BodyOf(response);
    }

    private static async Task<JsonElement> ReadAsync(GatewayClient client, SeededWorkspace workspace, Guid draftId)
    {
        var response = await client.GetAsync($"{DraftsIn(workspace)}/{draftId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private static Task<HttpResponseMessage> ApproveAsync(
        GatewayClient client, SeededWorkspace workspace, JsonElement draft, int revisionNumber) =>
        client.PostAsJsonAsync(
            $"{DraftsIn(workspace)}/{Id(draft)}/plan/approval",
            new { expectedConcurrencyToken = Token(draft), revisionNumber },
            Ct);

    /// <summary>
    /// A started draft with a plan on it, landed the way the worker lands one: through the content module's
    /// facade, against a real proposal row for the request the route queued.
    /// </summary>
    /// <param name="pinnedReference">
    /// For a newsletter, the reference of one recipe the plan is pinned to, so an item has a source to name.
    /// </param>
    private async Task<JsonElement> DraftWithPlanAsync(
        GatewayClient client, SeededWorkspace workspace, LongFormKind kind = LongFormKind.BlogArticle, Guid? pinnedReference = null)
    {
        var started = await StartAsync(client, workspace, kind: kind.ToString());
        var draft = started.GetProperty("draft");
        var requestId = started.GetProperty("request").GetProperty("aiProposalRequestId").GetGuid();

        await InAsync(workspace, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            var proposal = new AiProposal
            {
                Id = Guid.NewGuid(),
                AiOperationId = requestId,
                OutputSchemaVersion = "content.long-form-outline.v1",
                PromptTemplateId = AiTaskCatalog.LongFormOutline,
                PromptTemplateVersion = "1.0.0",
                PromptTemplateBodyChecksum = "sha256:" + new string('b', 64),
                ProviderName = "test-provider",
                ModelName = "test-model",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.AiProposals.Add(proposal);
            await db.SaveChangesAsync(Ct);

            var pins = new List<LongFormRecipePinInput>();

            if (pinnedReference is { } reference)
            {
                var (recipeId, versionId) = await CreativeContextSeeds.RecipeAsync(db, DateTimeOffset.UtcNow, Ct);
                pins.Add(new LongFormRecipePinInput(reference, recipeId, versionId));
            }

            var landed = await services.GetRequiredService<ILongFormDraftFacade>().RecordGeneratedPlanAsync(
                new LongFormGeneratedPlanInput(
                    Id(draft),
                    draft.GetProperty("creativeContextId").GetGuid(),
                    kind,
                    proposal.Id,
                    proposal.PromptTemplateId,
                    proposal.PromptTemplateVersion,
                    proposal.PromptTemplateBodyChecksum,
                    BrandStyleGuideVersionId: null,
                    CreativeContextVersion: null,
                    kind is LongFormKind.BlogArticle
                        ? [.. Article.Select(section => new LongFormGeneratedSectionInput(section.Role, section.Heading, section.Intent, null))]
                        : [new LongFormGeneratedSectionInput(LongFormSectionRole.Greeting, "Hello again", "Say hello.", null)],
                    pins),
                Ct);

            Assert.True(landed.Succeeded, landed.Error?.Code);

            return 0;
        });

        return await ReadAsync(client, workspace, Id(draft));
    }

    private Task<int> CountDraftsAsync(SeededWorkspace workspace) =>
        InAsync(workspace, services => services.GetRequiredService<CreatorPantryDbContext>().LongFormDrafts.CountAsync(Ct));

    private async Task<T> InAsync<T>(SeededWorkspace workspace, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider);
    }
}
