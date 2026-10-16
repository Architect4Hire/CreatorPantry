using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Planning;

/// <summary>
/// <c>PATCH .../work-tasks/{taskId}</c> through the real Gateway (13A.27): a field left out is untouched, one
/// sent as null is cleared, nothing is dropped as a side effect, a stale edit writes nothing, links and labels
/// are checked only when newly chosen, a task is handed on only by its owner or back to them, and a task is
/// nobody's to edit but theirs.
/// </summary>
public sealed class WorkTaskEditEndpointTests : IAsyncLifetime
{
    private const string Zone = "America/Chicago";

    /// <summary>
    /// Thursday lunchtime in <see cref="Zone"/>. Ahead of the real date on purpose: the gateway fixture's cookies
    /// are stamped from the fake clock and judged by the client's real one.
    /// </summary>
    private static readonly DateTimeOffset Thursday = new(2038, 10, 14, 17, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly ThisMonday = new(2038, 10, 11);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(time: new FakeTimeProvider(Thursday));

        await InWorkspaceAsync(_fixture.WorkspaceA, async db =>
        {
            db.BrandProfiles.Add(new BrandProfile
            {
                Id = Guid.NewGuid(),
                BrandName = "Sam's Kitchen",
                TimeZoneId = Zone,
                CreatedByMembershipId = Guid.NewGuid(),
                UpdatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Thursday,
                UpdatedAt = Thursday,
            });

            return await db.SaveChangesAsync(Ct);
        });
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Token(JsonElement task) => task.GetProperty("concurrencyToken").GetString()!;

    private static async Task<JsonElement> CaptureAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/work-tasks", body, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await BodyOf(response);
    }

    private static Task<HttpResponseMessage> PatchAsync(GatewayClient client, SeededWorkspace workspace, Guid taskId, object body) =>
        client.PatchAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/work-tasks/{taskId}", body, Ct);

    /// <summary>Patches and insists on a 200, returning the task as it now stands.</summary>
    private static async Task<JsonElement> EditedAsync(GatewayClient client, SeededWorkspace workspace, Guid taskId, object body)
    {
        var response = await PatchAsync(client, workspace, taskId, body);
        var task = await BodyOf(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)response.StatusCode}: {task}");

        return task;
    }

    private static Guid Id(JsonElement task) => task.GetProperty("id").GetGuid();

    private static bool IsNull(JsonElement task, string property) =>
        !task.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    // ---- Submitted fields ----

    [Fact]
    public async Task A_field_left_out_is_untouched_and_the_token_moves()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new
        {
            title = "Shoot the galette",
            description = "Morning light",
            priority = "High",
            scheduledDate = Iso(ThisMonday.AddDays(2)),
        });

        var edited = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            title = "  Shoot the plum galette  ",
        });

        Assert.Equal("Shoot the plum galette", edited.GetProperty("title").GetString());
        Assert.Equal("Morning light", edited.GetProperty("description").GetString());
        Assert.Equal("High", edited.GetProperty("priority").GetString());
        Assert.Equal(Iso(ThisMonday.AddDays(2)), edited.GetProperty("scheduledDate").GetString());
        Assert.Equal(Iso(ThisMonday), edited.GetProperty("planningWeekStart").GetString());

        // That the token moves is WorkTaskSqlServerTests' to prove: under SQLite a row version never changes.
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_nothing_and_keeps_the_token()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", priority = "High" });

        var onlyToken = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task) });
        Assert.Equal(Token(task), Token(onlyToken));

        var sameValues = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            title = "Shoot the galette",
            priority = "High",
        });
        Assert.Equal(Token(task), Token(sameValues));
    }

    [Fact]
    public async Task A_field_sent_as_null_is_cleared_and_a_blank_description_is_too()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", description = "Morning light" });

        var set = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            dueDate = Iso(ThisMonday.AddDays(4)),
            estimateMinutes = 90,
        });
        Assert.Equal(90, set.GetProperty("estimateMinutes").GetInt32());

        var cleared = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(set),
            dueDate = (string?)null,
            estimateMinutes = (int?)null,
            description = "   ",
        });

        Assert.True(IsNull(cleared, "dueDate"));
        Assert.True(IsNull(cleared, "estimateMinutes"));
        Assert.True(IsNull(cleared, "description"));
        Assert.Equal("Shoot the galette", cleared.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("title")]
    [InlineData("priority")]
    [InlineData("assigneeMembershipId")]
    public async Task A_field_a_task_always_has_cannot_be_cleared(string field)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var body = new Dictionary<string, object?> { ["expectedConcurrencyToken"] = Token(task), [field] = null };
        var response = await PatchAsync(client, _fixture.WorkspaceA, Id(task), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(PlanningErrorCodes.WorkTaskInvalid, Code(problem));
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("status", "Done")]
    [InlineData("position", 1)]
    public async Task An_edit_does_not_move_reorder_or_complete_a_task(string field, object value)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var body = new Dictionary<string, object?> { ["expectedConcurrencyToken"] = Token(task), [field] = value };
        var response = await PatchAsync(client, _fixture.WorkspaceA, Id(task), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(WorkTaskStatus.Backlog, (await StoredAsync(Id(task))).Status);
    }

    // ---- Days and weeks ----

    [Fact]
    public async Task A_day_brings_its_week_and_clearing_the_day_keeps_the_week()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var nextWednesday = ThisMonday.AddDays(9);
        var planned = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            scheduledDate = Iso(nextWednesday),
        });
        Assert.Equal(Iso(ThisMonday.AddDays(7)), planned.GetProperty("planningWeekStart").GetString());

        var unscheduled = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(planned),
            scheduledDate = (string?)null,
        });
        Assert.True(IsNull(unscheduled, "scheduledDate"));
        Assert.Equal(Iso(ThisMonday.AddDays(7)), unscheduled.GetProperty("planningWeekStart").GetString());
    }

    [Fact]
    public async Task Changing_the_week_never_drops_a_day_unless_the_day_is_sent_too()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", scheduledDate = Iso(ThisMonday.AddDays(2)) });

        var stranded = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            planningWeekStart = Iso(ThisMonday.AddDays(7)),
        });
        Assert.Equal(HttpStatusCode.BadRequest, stranded.StatusCode);
        Assert.True((await BodyOf(stranded)).GetProperty("errors").TryGetProperty("scheduledDate", out _));
        Assert.Equal(ThisMonday.AddDays(2), (await StoredAsync(Id(task))).ScheduledDate);

        var weekCleared = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            planningWeekStart = (string?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, weekCleared.StatusCode);

        var mismatched = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            planningWeekStart = Iso(ThisMonday.AddDays(7)),
            scheduledDate = Iso(ThisMonday.AddDays(1)),
        });
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);

        var moved = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            planningWeekStart = Iso(ThisMonday.AddDays(7)),
            scheduledDate = (string?)null,
        });
        Assert.Equal(Iso(ThisMonday.AddDays(7)), moved.GetProperty("planningWeekStart").GetString());
        Assert.True(IsNull(moved, "scheduledDate"));
    }

    [Fact]
    public async Task A_task_planned_for_another_day_goes_to_the_bottom_of_that_days_list()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Monday first", scheduledDate = Iso(ThisMonday) });
        await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Monday second", scheduledDate = Iso(ThisMonday) });
        var tuesday = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Was Tuesday", scheduledDate = Iso(ThisMonday.AddDays(1)) });

        await EditedAsync(client, _fixture.WorkspaceA, Id(tuesday), new
        {
            expectedConcurrencyToken = Token(tuesday),
            scheduledDate = Iso(ThisMonday),
        });

        var week = await WeekAsync(client, _fixture.WorkspaceA);
        Assert.Equal(["Monday first", "Monday second", "Was Tuesday"], DayTitles(week, 0));
    }

    // ---- Concurrency ----

    [Fact]
    public async Task A_stale_edit_is_refused_and_writes_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), title = "First edit" });

        // A token that could have been issued but is not the task's own: what a stale read looks like here, since
        // under SQLite a row version never moves. The race itself is WorkTaskSqlServerTests'.
        var stale = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = OtherTokenThan(Token(task)),
            title = "Composed against the old read",
            estimateMinutes = 30,
        });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskStale, Code(await BodyOf(stale)));

        var stored = await StoredAsync(Id(task));
        Assert.Equal("First edit", stored.Title);
        Assert.Null(stored.EstimateMinutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAA")]
    public async Task A_token_that_could_never_have_been_issued_is_a_bad_request(string? token)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var response = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = token, title = "Edited" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    // ---- Links ----

    [Fact]
    public async Task Links_are_set_replaced_and_cleared_and_none_goes_unless_sent_as_null()
    {
        var recipeId = await CreateRecipeAsync(_fixture.WorkspaceA);
        var otherRecipeId = await CreateRecipeAsync(_fixture.WorkspaceA);
        var cardId = await CreateCardAsync(_fixture.WorkspaceA);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var linked = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            recipeId,
            contentCardId = cardId,
        });
        Assert.Equal(recipeId, linked.GetProperty("links").GetProperty("recipeId").GetGuid());

        var replaced = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(linked),
            recipeId = otherRecipeId,
        });
        Assert.Equal(otherRecipeId, replaced.GetProperty("links").GetProperty("recipeId").GetGuid());
        Assert.Equal(cardId, replaced.GetProperty("links").GetProperty("contentCardId").GetGuid());

        var cleared = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(replaced),
            recipeId = (Guid?)null,
        });
        Assert.True(IsNull(cleared.GetProperty("links"), "recipeId"));
        Assert.Equal(cardId, cleared.GetProperty("links").GetProperty("contentCardId").GetGuid());
    }

    [Fact]
    public async Task Another_workspaces_record_is_refused_exactly_as_one_that_never_existed()
    {
        var theirs = await CreateRecipeAsync(_fixture.WorkspaceB);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var foreign = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), recipeId = theirs });
        var invented = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), recipeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invented.StatusCode);

        var (foreignBody, inventedBody) = (await BodyOf(foreign), await BodyOf(invented));
        Assert.Equal(PlanningErrorCodes.WorkTaskLinkUnprocessable, Code(foreignBody));
        Assert.Equal(
            inventedBody.GetProperty("errors").GetProperty("recipeId").ToString(),
            foreignBody.GetProperty("errors").GetProperty("recipeId").ToString());
        Assert.Null((await StoredAsync(Id(task))).RecipeId);
    }

    [Fact]
    public async Task A_link_the_task_already_has_is_not_checked_again()
    {
        var cardId = await CreateCardAsync(_fixture.WorkspaceA);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", contentCardId = cardId });

        // The card goes; the task's pointer stays, and an unrelated edit must not fail because of it.
        await InWorkspaceAsync(_fixture.WorkspaceA, db => db.ContentCards.Where(card => card.Id == cardId).ExecuteDeleteAsync(Ct));

        var edited = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), title = "Still about that card" });

        Assert.Equal(cardId, edited.GetProperty("links").GetProperty("contentCardId").GetGuid());
    }

    [Fact]
    public async Task A_step_is_never_dropped_by_changing_the_run_and_a_new_step_must_be_the_runs()
    {
        var (runId, stepKey) = await StartRunAsync(_fixture.WorkspaceA);
        var (otherRunId, _) = await StartRunAsync(_fixture.WorkspaceA);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", workflowRunId = runId, workflowStepKey = stepKey });

        var stranded = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), workflowRunId = otherRunId });
        Assert.Equal(HttpStatusCode.BadRequest, stranded.StatusCode);
        Assert.True((await BodyOf(stranded)).GetProperty("errors").TryGetProperty("workflowStepKey", out _));

        var noSuchStep = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), workflowStepKey = "no-such-step" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSuchStep.StatusCode);
        Assert.True((await BodyOf(noSuchStep)).GetProperty("errors").TryGetProperty("workflowStepKey", out _));

        var moved = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            workflowRunId = otherRunId,
            workflowStepKey = (string?)null,
        });
        Assert.Equal(otherRunId, moved.GetProperty("links").GetProperty("workflowRunId").GetGuid());
        Assert.True(IsNull(moved.GetProperty("links"), "workflowStepKey"));
    }

    // ---- Labels ----

    [Fact]
    public async Task Labels_are_replaced_as_a_set_cleared_and_shown_by_name_wherever_the_task_is()
    {
        var weeknight = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var photoDay = await SeedTagAsync(_fixture.WorkspaceA, "Photo day");

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette", scheduledDate = Iso(ThisMonday) });
        Assert.Empty(task.GetProperty("labels").EnumerateArray());

        var both = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            labelIds = new[] { weeknight, photoDay },
        });
        Assert.Equal(["Photo day", "Weeknight"], LabelNames(both));

        // The week shows them too.
        var week = await WeekAsync(client, _fixture.WorkspaceA);
        Assert.Equal(["Photo day", "Weeknight"], LabelNames(week.GetProperty("days")[0].GetProperty("tasks")[0]));

        var one = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(both), labelIds = new[] { weeknight } });
        Assert.Equal(["Weeknight"], LabelNames(one));

        var none = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(one), labelIds = (Guid[]?)null });
        Assert.Empty(LabelNames(none));
    }

    [Fact]
    public async Task A_label_edit_alone_is_refused_when_stale_like_any_other()
    {
        var weeknight = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var photoDay = await SeedTagAsync(_fixture.WorkspaceA, "Photo day");

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), labelIds = new[] { weeknight } });

        var stale = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = OtherTokenThan(Token(task)),
            labelIds = new[] { photoDay },
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal([weeknight], await LabelIdsAsync(Id(task)));
    }

    [Fact]
    public async Task A_retired_tag_already_carried_stays_but_one_newly_given_is_refused_as_another_workspaces_is()
    {
        var weeknight = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        var retired = await SeedTagAsync(_fixture.WorkspaceA, "Old habit", active: false);
        var theirs = await SeedTagAsync(_fixture.WorkspaceB, "Theirs");

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });
        var labelled = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), labelIds = new[] { weeknight } });

        // Retired after it was given: an unrelated edit, and an edit naming it again, both still succeed.
        await InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkspaceTags.Where(tag => tag.Id == weeknight)
            .ExecuteUpdateAsync(set => set.SetProperty(tag => tag.IsActive, false), Ct));

        var renamed = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(labelled), title = "Renamed" });
        Assert.Equal(["Weeknight"], LabelNames(renamed));
        var kept = await EditedAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(renamed), labelIds = new[] { weeknight } });
        Assert.Equal(Token(renamed), Token(kept));

        foreach (var refusedTag in new[] { retired, theirs, Guid.NewGuid() })
        {
            var refused = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new
            {
                expectedConcurrencyToken = Token(kept),
                labelIds = new[] { weeknight, refusedTag },
            });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal(PlanningErrorCodes.WorkTaskLabelUnprocessable, Code(await BodyOf(refused)));
        }

        Assert.Equal([weeknight], await LabelIdsAsync(Id(task)));
    }

    [Fact]
    public async Task Too_many_or_repeated_labels_are_a_bad_request()
    {
        var weeknight = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Shoot the galette" });

        var repeated = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), labelIds = new[] { weeknight, weeknight } });
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);

        var tooMany = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            labelIds = Enumerable.Range(0, WorkTaskPolicy.MaxLabels + 1).Select(_ => Guid.NewGuid()).ToArray(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    // ---- Handing a task on ----

    [Fact]
    public async Task The_owner_hands_a_task_on_and_it_joins_the_bottom_of_their_list_and_they_can_hand_it_back()
    {
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var owner = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Owner);

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        await CaptureAsync(asEditor, _fixture.WorkspaceA, new { title = "The editor's own", scheduledDate = Iso(ThisMonday) });
        var task = await CaptureAsync(asOwner, _fixture.WorkspaceA, new { title = "Handed on", scheduledDate = Iso(ThisMonday) });

        var handed = await EditedAsync(asOwner, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            assigneeMembershipId = editor,
        });
        Assert.Equal(editor, handed.GetProperty("assignee").GetProperty("membershipId").GetGuid());
        Assert.Equal(owner, handed.GetProperty("owner").GetProperty("membershipId").GetGuid());

        Assert.Equal(["The editor's own", "Handed on"], DayTitles(await WeekAsync(asEditor, _fixture.WorkspaceA), 0));
        Assert.Empty(DayTitles(await WeekAsync(asOwner, _fixture.WorkspaceA), 0));

        var back = await EditedAsync(asEditor, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(handed),
            assigneeMembershipId = owner,
        });
        Assert.Equal(owner, back.GetProperty("assignee").GetProperty("membershipId").GetGuid());
    }

    [Fact]
    public async Task The_person_doing_a_task_may_hand_it_back_and_to_nobody_else()
    {
        var editor = await MembershipAsync(_fixture.WorkspaceA, WorkspaceRole.Editor);
        var third = await AddMemberAsync(_fixture.WorkspaceA, WorkspaceRole.Contributor);

        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var task = await CaptureAsync(asOwner, _fixture.WorkspaceA, new { title = "Handed on" });
        var handed = await EditedAsync(asOwner, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), assigneeMembershipId = editor });

        var passedOn = await PatchAsync(asEditor, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(handed), assigneeMembershipId = third });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, passedOn.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskAssigneeUnprocessable, Code(await BodyOf(passedOn)));
        Assert.Equal(editor, (await StoredAsync(Id(task))).AssigneeMembershipId);

        // The owner, though, may hand it to anyone who can keep work.
        var toThird = await EditedAsync(asOwner, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(handed), assigneeMembershipId = third });
        Assert.Equal(third, toThird.GetProperty("assignee").GetProperty("membershipId").GetGuid());
    }

    [Fact]
    public async Task A_task_cannot_be_handed_to_a_viewer_a_removed_member_or_another_workspaces_member()
    {
        var viewer = await AddMemberAsync(_fixture.WorkspaceA, WorkspaceRole.Viewer);
        var removed = await AddMemberAsync(_fixture.WorkspaceA, WorkspaceRole.Editor, WorkspaceMembershipStatus.Removed);
        var elsewhere = await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Owner);

        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var task = await CaptureAsync(client, _fixture.WorkspaceA, new { title = "Handed on" });

        foreach (var target in new[] { viewer, removed, elsewhere, Guid.NewGuid() })
        {
            var response = await PatchAsync(client, _fixture.WorkspaceA, Id(task), new { expectedConcurrencyToken = Token(task), assigneeMembershipId = target });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(PlanningErrorCodes.WorkTaskAssigneeUnprocessable, Code(await BodyOf(response)));
        }
    }

    // ---- Whose task it is ----

    [Fact]
    public async Task Anyone_but_the_owner_or_the_person_doing_it_is_told_it_does_not_exist()
    {
        using var asOwner = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var asEditor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);
        using var ownerOfB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var task = await CaptureAsync(asOwner, _fixture.WorkspaceA, new { title = "The owner's own" });
        var body = new { expectedConcurrencyToken = Token(task), title = "Taken over" };

        var colleague = await PatchAsync(asEditor, _fixture.WorkspaceA, Id(task), body);
        Assert.Equal(HttpStatusCode.NotFound, colleague.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskNotFound, Code(await BodyOf(colleague)));

        // Not even a request that would be refused for what it asks: whose task it is is settled first, so a
        // colleague cannot learn about it from which refusal comes back.
        var probing = await PatchAsync(asEditor, _fixture.WorkspaceA, Id(task), new
        {
            expectedConcurrencyToken = Token(task),
            labelIds = new[] { Guid.NewGuid() },
            assigneeMembershipId = Guid.NewGuid(),
            recipeId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.NotFound, probing.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskNotFound, Code(await BodyOf(probing)));

        var unknown = await PatchAsync(asOwner, _fixture.WorkspaceA, Guid.NewGuid(), body);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(PlanningErrorCodes.WorkTaskNotFound, Code(await BodyOf(unknown)));

        // Another workspace: through its own slug the task is not there, and through A's the caller is not a member.
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(ownerOfB, _fixture.WorkspaceB, Id(task), body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(ownerOfB, _fixture.WorkspaceA, Id(task), body)).StatusCode);

        Assert.Equal("The owner's own", (await StoredAsync(Id(task))).Title);
    }

    [Fact]
    public async Task A_viewer_may_not_edit_and_the_task_is_untouched()
    {
        Assert.Equal(WorkspaceRole.Viewer, _fixture.WorkspaceB.MemberRole);
        var viewer = await MembershipAsync(_fixture.WorkspaceB, WorkspaceRole.Viewer);
        var task = await SeedTaskAsync(_fixture.WorkspaceB, viewer, "Handed to the viewer before they were made one");

        using var client = await SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var response = await PatchAsync(client, _fixture.WorkspaceB, task.Id, new
        {
            expectedConcurrencyToken = WorkTaskConcurrencyToken.From(task.RowVersion),
            title = "Edited",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Helpers ----

    /// <summary>A well-formed token that is not <paramref name="token"/>.</summary>
    private static string OtherTokenThan(string token)
    {
        var bytes = Convert.FromBase64String(token);
        bytes[^1] ^= 0xFF;

        return Convert.ToBase64String(bytes);
    }

    private static IReadOnlyList<string> LabelNames(JsonElement task) =>
        [.. task.GetProperty("labels").EnumerateArray().Select(label => label.GetProperty("name").GetString()!)];

    private static IReadOnlyList<string> DayTitles(JsonElement week, int offset) =>
        [.. week.GetProperty("days")[offset].GetProperty("tasks").EnumerateArray().Select(task => task.GetProperty("title").GetString()!)];

    private static async Task<JsonElement> WeekAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.GetAsync($"/api/v1/workspaces/{workspace.Slug}/work-tasks/week", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private Task<WorkTask> StoredAsync(Guid taskId) =>
        InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTasks.AsNoTracking().SingleAsync(task => task.Id == taskId, Ct));

    private Task<List<Guid>> LabelIdsAsync(Guid taskId) =>
        InWorkspaceAsync(_fixture.WorkspaceA, db => db.WorkTaskLabels.AsNoTracking()
            .Where(label => label.WorkTaskId == taskId).Select(label => label.WorkspaceTagId).ToListAsync(Ct));

    private Task<Guid> SeedTagAsync(SeededWorkspace workspace, string name, bool active = true) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var tag = new WorkspaceTag
            {
                Id = Guid.NewGuid(),
                Name = name,
                NormalizedName = name.ToLowerInvariant(),
                IsActive = active,
                CreatedAt = Thursday,
            };
            db.WorkspaceTags.Add(tag);
            await db.SaveChangesAsync(Ct);

            return tag.Id;
        });

    private Task<WorkTask> SeedTaskAsync(SeededWorkspace workspace, Guid assignee, string title) =>
        InWorkspaceAsync(workspace, async db =>
        {
            var task = new WorkTask
            {
                Id = Guid.NewGuid(),
                OwnerMembershipId = assignee,
                AssigneeMembershipId = assignee,
                Title = title,
                SortRank = WorkTaskPolicy.RankGap,
                CreatedAt = Thursday,
                UpdatedAt = Thursday,
            };
            db.WorkTasks.Add(task);
            await db.SaveChangesAsync(Ct);

            return task;
        });

    /// <summary>A further member of a workspace, with a user of their own, in any role and state.</summary>
    private async Task<Guid> AddMemberAsync(
        SeededWorkspace workspace, WorkspaceRole role, WorkspaceMembershipStatus status = WorkspaceMembershipStatus.Active)
    {
        var userId = await _fixture.Api.CreateUserAsync($"{Guid.NewGuid():N}@example.test", "correct horse battery");

        return await InWorkspaceAsync(workspace, async db =>
        {
            var membership = new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Role = role,
                Status = status,
                JoinedAt = Thursday,
            };
            db.WorkspaceMemberships.Add(membership);
            await db.SaveChangesAsync(Ct);

            return membership.Id;
        });
    }

    private Task<Guid> MembershipAsync(SeededWorkspace workspace, WorkspaceRole role) =>
        InWorkspaceAsync(workspace, db => db.WorkspaceMemberships.AsNoTracking()
            .Where(membership => membership.WorkspaceId == workspace.Id && membership.Role == role)
            .Select(membership => membership.Id)
            .SingleAsync(Ct));

    private async Task<Guid> CreateRecipeAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", new { title = "Plum galette" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    private async Task<Guid> CreateCardAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/content-cards", new { title = "Autumn galette post" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("card").GetProperty("id").GetGuid();
    }

    /// <summary>A real run, so its steps are the ones a task may name.</summary>
    private async Task<(Guid RunId, string StepKey)> StartRunAsync(SeededWorkspace workspace)
    {
        using var client = await SignInAsync(workspace.OwnerEmail);
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/workflow-runs",
            new { definitionKey = "images.create" },
            Guid.NewGuid().ToString(),
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await BodyOf(response);

        return (run.GetProperty("id").GetGuid(), run.GetProperty("steps")[0].GetProperty("key").GetString()!);
    }

    /// <summary>Work inside one workspace's resolved context, the way a request would run.</summary>
    private async Task<T> InWorkspaceAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
