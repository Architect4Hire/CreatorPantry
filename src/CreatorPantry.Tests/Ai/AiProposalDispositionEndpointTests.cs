using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// <c>POST .../ai-proposals/{id}/disposition</c> through the real Gateway: accepting everything, accepting a
/// selection, rejecting, feedback, and every way a disposition is refused without writing anything.
/// </summary>
/// <remarks>
/// <para>
/// The proposal is seeded straight into the database rather than generated, because no worker exists yet to run
/// a task and no task is enabled. What is under test is the disposition, and a seeded proposal is
/// indistinguishable from a produced one at the point this route reads it.
/// </para>
/// <para>
/// Almost every test here also asserts what is <em>not</em> written. "Fails without mutation" is the load-bearing
/// half of AIREC-007, and a refusal that had already applied one of the accepted changes would pass an assertion
/// about its status code.
/// </para>
/// </remarks>
public sealed class AiProposalDispositionEndpointTests : IAsyncLifetime
{
    /// <summary>
    /// The shared fixture seeds Owner plus Editor in workspace A, and the role that must be refused here is
    /// below both, so it has to be added.
    /// </summary>
    private const string ViewerEmail = "disposition-viewer-a@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        var userId = await _fixture.Api.CreateUserAsync(ViewerEmail, Password);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Viewer,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- accept all ------------------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_everything_writes_one_new_version_with_the_proposal_on_it()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptAll),
            acceptedChangeIds = seeded.ChangeIds,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(nameof(AiOperationStatus.Accepted), body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("acceptedChangeCount").GetInt32());
        Assert.Equal(0, body.GetProperty("rejectedChangeCount").GetInt32());
        Assert.Equal(2, body.GetProperty("recipeVersionNumber").GetInt32());

        var recipe = await ReadRecipeAsync(client, seeded.RecipeId);

        Assert.Equal("A warmer opening.", recipe.GetProperty("headnote").GetString());
        Assert.Equal("Plum and olive oil cake", recipe.GetProperty("title").GetString());

        // Provenance, which is the whole reason RecipeVersionSource has this member: a version a model helped
        // write stays identifiable afterwards rather than looking like a creator's own edit.
        var version = recipe.GetProperty("currentVersion");
        Assert.Equal(2, version.GetProperty("versionNumber").GetInt32());
        Assert.Equal(nameof(RecipeVersionSource.AiProposalAccepted), version.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Accepting_everything_records_every_change_as_accepted()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        await AcceptAllAsync(client, seeded);

        Assert.All(
            await ChangesAsync(seeded.ProposalId),
            change =>
            {
                Assert.Equal(AiChangeDisposition.Accepted, change.Disposition);
                Assert.NotNull(change.DecidedAt);
                Assert.NotNull(change.DecidedByMembershipId);
            });
    }

    /// <summary>
    /// What makes accept-all a confirmation rather than a flag. A client naming fewer changes than the proposal
    /// holds is looking at something other than this proposal, and taking its word would apply changes nobody
    /// reviewed.
    /// </summary>
    [Fact]
    public async Task Accepting_everything_must_name_everything()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptAll),
            acceptedChangeIds = new[] { seeded.ChangeIds[0] },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingHappenedAsync(client, seeded);
    }

    // ---- accept selected -------------------------------------------------------------------------------

    [Fact]
    public async Task Accepting_a_selection_applies_only_what_was_selected()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptSelected),
            acceptedChangeIds = new[] { seeded.HeadnoteChangeId },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(nameof(AiOperationStatus.PartiallyAccepted), body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("acceptedChangeCount").GetInt32());
        Assert.Equal(1, body.GetProperty("rejectedChangeCount").GetInt32());

        var recipe = await ReadRecipeAsync(client, seeded.RecipeId);

        Assert.Equal("A warmer opening.", recipe.GetProperty("headnote").GetString());
        Assert.Equal("Olive oil cake", recipe.GetProperty("title").GetString());
    }

    /// <summary>
    /// A proposal leaves review with nothing still pending. "Pending" on a terminal operation would mean nobody
    /// ever decided, and the declined suggestion is worth keeping — a creator can see what they turned down.
    /// </summary>
    [Fact]
    public async Task What_was_not_selected_is_recorded_as_rejected()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptSelected),
            acceptedChangeIds = new[] { seeded.HeadnoteChangeId },
        });

        var changes = await ChangesAsync(seeded.ProposalId);

        Assert.Equal(
            AiChangeDisposition.Accepted,
            changes.Single(change => change.Id == seeded.HeadnoteChangeId).Disposition);

        Assert.Equal(
            AiChangeDisposition.Rejected,
            changes.Single(change => change.Id != seeded.HeadnoteChangeId).Disposition);

        Assert.DoesNotContain(changes, change => change.Disposition is AiChangeDisposition.Pending);
    }

    // ---- reject ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejecting_writes_no_version_and_changes_nothing()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.Reject),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(nameof(AiOperationStatus.Rejected), body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("acceptedChangeCount").GetInt32());
        Assert.Equal(2, body.GetProperty("rejectedChangeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("recipeVersionNumber").ValueKind);

        var recipe = await ReadRecipeAsync(client, seeded.RecipeId);
        Assert.Equal("Olive oil cake", recipe.GetProperty("title").GetString());
        Assert.Equal(1, recipe.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// A rejection that also names changes is a client that has not decided what it is asking for. One of the two
    /// readings writes to the creator's recipe, so refusing is the only safe answer.
    /// </summary>
    [Fact]
    public async Task A_rejection_cannot_also_accept_changes()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.Reject),
            acceptedChangeIds = seeded.ChangeIds,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingHappenedAsync(client, seeded);
    }

    // ---- stale source ----------------------------------------------------------------------------------

    /// <summary>
    /// The restriction. Every before value the creator was shown was read from the pinned version, so applying
    /// their decision to a newer one would apply it to text they never read. Failing is the only honest answer.
    /// </summary>
    [Fact]
    public async Task A_proposal_whose_source_moved_on_cannot_be_accepted()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        await EditAsync(client, seeded.RecipeId, seeded.ConcurrencyToken, "Second thoughts.");

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptAll),
            acceptedChangeIds = seeded.ChangeIds,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Nothing at all: not the version, not the dispositions, not the status. The transaction the disposition
        // opens is what makes that true across two modules.
        var recipe = await ReadRecipeAsync(client, seeded.RecipeId);
        Assert.Equal("Olive oil cake", recipe.GetProperty("title").GetString());
        Assert.Equal(2, recipe.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());

        Assert.All(
            await ChangesAsync(seeded.ProposalId),
            change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));

        Assert.Equal(AiOperationStatus.Proposed, (await OperationAsync(seeded.RequestId)).Status);
    }

    /// <summary>Rejecting a stale proposal is fine: nothing is applied, so nothing can be applied to the wrong thing.</summary>
    [Fact]
    public async Task A_stale_proposal_can_still_be_rejected()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        await EditAsync(client, seeded.RecipeId, seeded.ConcurrencyToken, "Second thoughts.");

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.Reject),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AiOperationStatus.Rejected, (await OperationAsync(seeded.RequestId)).Status);
    }

    // ---- invalid selections ----------------------------------------------------------------------------

    [Fact]
    public async Task A_change_that_does_not_belong_to_this_proposal_is_refused()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptSelected),
            acceptedChangeIds = new[] { Guid.NewGuid() },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            AiProposalErrors.SelectionInvalid,
            (await BodyOf(response)).GetProperty("code").GetString());

        await AssertNothingHappenedAsync(client, seeded);
    }

    /// <summary>
    /// A selection that is valid except for one id fails whole. Applying the valid part would leave the creator
    /// with a recipe edited in a way they never confirmed.
    /// </summary>
    [Fact]
    public async Task A_selection_with_one_bad_id_applies_none_of_it()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptSelected),
            acceptedChangeIds = new[] { seeded.HeadnoteChangeId, Guid.NewGuid() },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingHappenedAsync(client, seeded);
    }

    [Fact]
    public async Task An_accept_that_names_no_change_is_refused()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptSelected),
            acceptedChangeIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingHappenedAsync(client, seeded);
    }

    [Fact]
    public async Task A_decision_that_says_nothing_is_refused()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await DispositionAsync(client, seeded, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingHappenedAsync(client, seeded);
    }

    // ---- replay ----------------------------------------------------------------------------------------

    /// <summary>
    /// A retried request whose first response was lost must not buy a second version of the recipe. The recorded
    /// decision is compared with the one being asked for, which survives longer than an idempotency record would.
    /// </summary>
    [Fact]
    public async Task Replaying_the_same_decision_writes_nothing_a_second_time()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        await AcceptAllAsync(client, seeded);

        var replay = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptAll),
            acceptedChangeIds = seeded.ChangeIds,
        });

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var body = await BodyOf(replay);
        Assert.Equal(nameof(AiOperationStatus.Accepted), body.GetProperty("status").GetString());

        // No version, because this call wrote none. The first one did, and a reply claiming otherwise would be
        // reporting work it did not do.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("recipeVersionNumber").ValueKind);

        // Still exactly one new version: the replay did not apply the changes again.
        var recipe = await ReadRecipeAsync(client, seeded.RecipeId);
        Assert.Equal(2, recipe.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// A second request asking for a <em>different</em> decision is refused. Quietly serving the earlier outcome
    /// would tell a creator their selection had been applied when a different one had.
    /// </summary>
    [Fact]
    public async Task A_different_decision_after_one_was_recorded_is_refused()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        await AcceptAllAsync(client, seeded);

        var second = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.Reject),
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(
            AiProposalErrors.ProposalDecided,
            (await BodyOf(second)).GetProperty("code").GetString());

        Assert.Equal(AiOperationStatus.Accepted, (await OperationAsync(seeded.RequestId)).Status);
    }

    // ---- feedback --------------------------------------------------------------------------------------

    [Fact]
    public async Task Feedback_is_recorded_alongside_the_decision()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.Reject),
            wasHelpful = false,
            comment = "It rewrote the headnote in someone else's voice.",
        });

        var feedback = Assert.Single(await FeedbackAsync(seeded.ProposalId));

        Assert.False(feedback.WasHelpful);
        Assert.Equal("It rewrote the headnote in someone else's voice.", feedback.Comment);
    }

    /// <summary>
    /// Feedback that says nothing is not feedback. A check constraint refuses such a row, so a disposition with
    /// no opinion must write none rather than one recording that someone opened the panel.
    /// </summary>
    [Fact]
    public async Task A_decision_with_no_opinion_leaves_no_feedback()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        await AcceptAllAsync(client, seeded);

        Assert.Empty(await FeedbackAsync(seeded.ProposalId));
    }

    // ---- what cannot be decided ------------------------------------------------------------------------

    [Fact]
    public async Task A_request_that_has_produced_no_proposal_cannot_be_decided()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client, status: AiOperationStatus.Requested, withProposal: false);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.Reject),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            AiProposalErrors.ProposalNotFound,
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_request_that_does_not_exist_is_not_found()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var response = await client.PostAsJsonAsync(
            $"{Route(_fixture.WorkspaceA, seeded.RecipeId)}/{Guid.NewGuid()}/disposition",
            new { decision = nameof(AiDispositionDecision.Reject) },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- shapes beyond a header field ------------------------------------------------------------------

    /// <summary>
    /// A step change goes through the recipe's instruction reconciler, which means the whole method is re-emitted
    /// from the aggregate and the one changed field lands in place. The rest of the step survives.
    /// </summary>
    [Fact]
    public async Task Accepting_an_instruction_step_change_rewrites_that_step()
    {
        using var client = await SignInAsync();

        var seeded = await SeedAsync(client, changes: detail =>
        [
            new()
            {
                Id = Guid.NewGuid(),
                ChangeKind = AiChangeKind.Set,
                TargetKind = AiChangeTargetKind.InstructionStep,
                TargetId = StepIdOf(detail),
                FieldName = "text",
                BeforeValue = "Whisk the eggs.",
                AfterValue = "Whisk the eggs until pale.",
            },
        ]);

        var response = await AcceptSelectedAsync(client, seeded, seeded.ChangeIds);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var step = (await ReadRecipeAsync(client, seeded.RecipeId))
            .GetProperty("instructionGroups").EnumerateArray().Single()
            .GetProperty("steps").EnumerateArray().Single();

        Assert.Equal("Whisk the eggs until pale.", step.GetProperty("text").GetString());

        // The same step, updated in place. A new id would mean the reconciler deleted the creator's step and
        // wrote a replacement, which is not what accepting a reworded sentence should do.
        Assert.Equal(StepIdOf(seeded.Detail), step.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Accepting_a_tag_addition_adds_that_tag()
    {
        using var client = await SignInAsync();

        var seeded = await SeedAsync(client, changes: _ =>
        [
            new()
            {
                Id = Guid.NewGuid(),
                ChangeKind = AiChangeKind.Add,
                TargetKind = AiChangeTargetKind.Tag,
                AfterValue = "weeknight",
            },
        ]);

        Assert.Equal(HttpStatusCode.OK, (await AcceptSelectedAsync(client, seeded, seeded.ChangeIds)).StatusCode);

        var tags = (await ReadRecipeAsync(client, seeded.RecipeId))
            .GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .ToArray();

        Assert.Equal(["weeknight"], tags);
    }

    /// <summary>
    /// The finding an audit of this seam turned up. A temperature with no unit is refused by the database — "bake
    /// at 180 g" is a food-safety-adjacent fact recorded wrongly — and a change row has nowhere to carry a unit.
    /// The diff calculator refuses to offer one; this is the second line, and it must be a refusal rather than the
    /// constraint violation arriving as a 500.
    /// </summary>
    [Fact]
    public async Task A_temperature_on_a_step_with_no_unit_cannot_be_accepted()
    {
        using var client = await SignInAsync();

        var seeded = await SeedAsync(client, changes: detail =>
        [
            new()
            {
                Id = Guid.NewGuid(),
                ChangeKind = AiChangeKind.Set,
                TargetKind = AiChangeTargetKind.InstructionStep,
                TargetId = StepIdOf(detail),
                FieldName = "temperatureValue",
                AfterValue = "180",
            },
        ]);

        var response = await AcceptSelectedAsync(client, seeded, seeded.ChangeIds);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingHappenedAsync(client, seeded);
    }

    /// <summary>
    /// A value longer than the field's own limit is refused rather than reaching the column as a truncation error,
    /// which the recipe data layer correctly declines to call a conflict and rethrows — a 500 where the honest
    /// answer names the field.
    /// </summary>
    [Fact]
    public async Task An_over_long_accepted_value_is_refused()
    {
        using var client = await SignInAsync();

        var seeded = await SeedAsync(client, changes: _ =>
        [
            new()
            {
                Id = Guid.NewGuid(),
                ChangeKind = AiChangeKind.Set,
                TargetKind = AiChangeTargetKind.Recipe,
                FieldName = "title",
                BeforeValue = "Olive oil cake",
                AfterValue = new string('x', RecipePolicy.TitleMaxLength + 1),
            },
        ]);

        var response = await AcceptSelectedAsync(client, seeded, seeded.ChangeIds);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingHappenedAsync(client, seeded);
    }

    /// <summary>
    /// A stored change whose target the recipe seam cannot express. <c>AiChangeApplicability</c> stops one being
    /// written, so this can only arrive from a row that predates the gate — and it must answer rather than throw
    /// inside the transaction.
    /// </summary>
    [Fact]
    public async Task A_change_the_recipe_seam_cannot_express_is_refused()
    {
        using var client = await SignInAsync();

        var seeded = await SeedAsync(client, changes: _ =>
        [
            new()
            {
                Id = Guid.NewGuid(),
                ChangeKind = AiChangeKind.Set,
                TargetKind = AiChangeTargetKind.Ingredient,
                TargetId = Guid.NewGuid(),
                FieldName = "quantity",
                AfterValue = "3",
            },
        ]);

        var response = await AcceptSelectedAsync(client, seeded, seeded.ChangeIds);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            AiProposalErrors.SelectionInvalid,
            (await BodyOf(response)).GetProperty("code").GetString());

        await AssertNothingHappenedAsync(client, seeded);
    }

    /// <summary>
    /// An archived recipe accepts no content change, and a proposal is no exception. The refusal comes from the
    /// recipe module's own rule, reached because accepted changes travel the ordinary merge path.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_cannot_absorb_an_accepted_proposal()
    {
        using var client = await SignInAsync();
        var seeded = await SeedAsync(client);

        var archived = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes/{seeded.RecipeId}/archive",
            new { expectedConcurrencyToken = seeded.ConcurrencyToken },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptAll),
            acceptedChangeIds = seeded.ChangeIds,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.RecipeArchivedConflict,
            (await BodyOf(response)).GetProperty("code").GetString());

        // The decision is not recorded either: a proposal refused for the recipe's state is still awaiting one.
        Assert.All(
            await ChangesAsync(seeded.ProposalId),
            change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));

        Assert.Equal(AiOperationStatus.Proposed, (await OperationAsync(seeded.RequestId)).Status);
    }

    // ---- authorization --------------------------------------------------------------------------------

    /// <summary>
    /// A Viewer cannot decide a proposal, and cannot reject one either. Rejecting reaches no recipe, but it does
    /// decide the fate of generated content aimed at the workspace's own, and the role that may not change a
    /// recipe should not be the role that closes a proposal against it.
    /// </summary>
    [Fact]
    public async Task A_viewer_cannot_disposition_a_proposal()
    {
        using var owner = await SignInAsync();
        var seeded = await SeedAsync(owner);

        using var viewer = await SignInAsync(ViewerEmail, Password);

        var response = await DispositionAsync(viewer, seeded, new
        {
            decision = nameof(AiDispositionDecision.Reject),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNothingHappenedAsync(owner, seeded);
    }

    // ---- the workspace boundary ------------------------------------------------------------------------

    /// <summary>
    /// Workspace B cannot decide A's proposal, and is told it does not exist rather than that it may not. The
    /// operation stays exactly as it was.
    /// </summary>
    [Fact]
    public async Task Another_workspace_cannot_disposition_this_proposal()
    {
        using var ownerOfA = await SignInAsync();
        var seeded = await SeedAsync(ownerOfA);

        using var ownerOfB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        var response = await ownerOfB.PostAsJsonAsync(
            $"{Route(_fixture.WorkspaceB, seeded.RecipeId)}/{seeded.RequestId}/disposition",
            new
            {
                decision = nameof(AiDispositionDecision.AcceptAll),
                acceptedChangeIds = seeded.ChangeIds,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(AiOperationStatus.Proposed, (await OperationAsync(seeded.RequestId)).Status);
        Assert.All(
            await ChangesAsync(seeded.ProposalId),
            change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));
    }

    /// <summary>
    /// Nor through its own route with A's recipe id. Both halves of the address have to belong to the resolved
    /// workspace, and neither refusal says which one failed.
    /// </summary>
    [Fact]
    public async Task A_recipe_from_another_workspace_is_not_found()
    {
        using var ownerOfA = await SignInAsync();
        var seeded = await SeedAsync(ownerOfA);

        var response = await ownerOfA.PostAsJsonAsync(
            $"{Route(_fixture.WorkspaceA, Guid.NewGuid())}/{seeded.RequestId}/disposition",
            new { decision = nameof(AiDispositionDecision.Reject) },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <param name="Detail">
    /// The recipe as the API returned it, so a test can find the step or group id a seeded change addresses.
    /// </param>
    private sealed record SeededProposal(
        Guid RecipeId,
        string ConcurrencyToken,
        Guid RequestId,
        Guid ProposalId,
        IReadOnlyList<Guid> ChangeIds,
        JsonElement Detail)
    {
        /// <summary>The first and second seeded changes, named for the default seed's two header changes.</summary>
        public Guid HeadnoteChangeId => ChangeIds[0];

        public Guid TitleChangeId => ChangeIds[1];
    }

    private Task<GatewayClient> SignInAsync(string? email = null, string? password = null) =>
        _fixture.SignInAsync(
            email ?? _fixture.WorkspaceA.OwnerEmail,
            password ?? Password,
            TestContext.Current.CancellationToken);

    private static string Route(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/ai-proposals";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> DispositionAsync(
        GatewayClient client,
        SeededProposal seeded,
        object body) =>
        client.PostAsJsonAsync(
            $"{Route(_fixture.WorkspaceA, seeded.RecipeId)}/{seeded.RequestId}/disposition",
            body,
            TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> AcceptSelectedAsync(
        GatewayClient client,
        SeededProposal seeded,
        IReadOnlyList<Guid> acceptedChangeIds) =>
        DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptSelected),
            acceptedChangeIds,
        });

    private async Task AcceptAllAsync(GatewayClient client, SeededProposal seeded)
    {
        var response = await DispositionAsync(client, seeded, new
        {
            decision = nameof(AiDispositionDecision.AcceptAll),
            acceptedChangeIds = seeded.ChangeIds,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<JsonElement> ReadRecipeAsync(GatewayClient client, Guid recipeId) =>
        await BodyOf(await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes/{recipeId}",
            TestContext.Current.CancellationToken));

    /// <summary>
    /// Edits the recipe the ordinary way, which is how a proposal's pinned source comes to be stale.
    /// </summary>
    /// <remarks>
    /// A real edit through the real route rather than a poke at the database: what makes the proposal stale is
    /// the recipe having a newer current version, and only the write seam produces one.
    /// </remarks>
    private async Task EditAsync(GatewayClient client, Guid recipeId, string token, string notes)
    {
        var response = await client.PatchAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes/{recipeId}",
            new { expectedConcurrencyToken = token, notes },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Refusals write nothing at all: the recipe stands, every change is still pending, and the operation is
    /// still awaiting a decision.
    /// </summary>
    private async Task AssertNothingHappenedAsync(GatewayClient client, SeededProposal seeded)
    {
        var recipe = await ReadRecipeAsync(client, seeded.RecipeId);

        Assert.Equal("Olive oil cake", recipe.GetProperty("title").GetString());
        Assert.Equal(1, recipe.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());

        Assert.All(
            await ChangesAsync(seeded.ProposalId),
            change => Assert.Equal(AiChangeDisposition.Pending, change.Disposition));

        Assert.Equal(AiOperationStatus.Proposed, (await OperationAsync(seeded.RequestId)).Status);
    }

    /// <summary>
    /// Creates a recipe through the API, then seeds a proposal against its current version.
    /// </summary>
    /// <remarks>
    /// The recipe goes through the real route so that it, its version and its snapshot are exactly what the
    /// product writes. Only the proposal is seeded, because producing one needs a worker and an enabled task,
    /// and neither is what this file is about.
    /// </remarks>
    /// <param name="changes">
    /// The changes the proposal offers, built from the created recipe's detail so they can address its real step
    /// and group ids. Defaults to the two header changes most of this file uses.
    /// </param>
    /// <remarks>
    /// A seeded change deliberately bypasses <c>AiDiffCalculator</c>, which is how the second-line refusals on the
    /// apply path can be reached at all: those exist for a proposal row written before a gate was added, and the
    /// only way to produce one is to write it directly.
    /// </remarks>
    private async Task<SeededProposal> SeedAsync(
        GatewayClient client,
        AiOperationStatus status = AiOperationStatus.Proposed,
        bool withProposal = true,
        Func<JsonElement, AiStructuredChange[]>? changes = null)
    {
        var cancellation = TestContext.Current.CancellationToken;

        var created = await client.PostAsJsonAsync(
            Recipes(),
            new
            {
                title = "Olive oil cake",
                headnote = "The one my grandmother made.",
                instructions = new object[]
                {
                    new { title = "Batter", steps = new object[] { new { text = "Whisk the eggs." } } },
                },
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), cancellation));

        var recipeId = detail.GetProperty("id").GetGuid();
        var versionId = detail.GetProperty("currentVersion").GetProperty("id").GetGuid();

        var requestId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var offered = (changes ?? DefaultChanges)(detail);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.AiOperations.Add(new AiOperation
        {
            Id = requestId,
            WorkspaceId = _fixture.WorkspaceA.Id,
            TaskType = AiTaskType.Diagnostic,
            Scope = AiOperationScope.WholeRecipe,
            Status = status,
            RecipeId = recipeId,
            RecipeVersionId = versionId,
            IdempotencyKey = $"seed-{requestId:N}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        if (withProposal)
        {
            var proposal = new AiProposal
            {
                Id = proposalId,
                WorkspaceId = _fixture.WorkspaceA.Id,
                AiOperationId = requestId,
                SourceRecipeVersionId = versionId,
                OutputSchemaVersion = "fixture.diagnostic.v1",
                PromptTemplateId = "fixture.diagnostic",
                PromptTemplateVersion = "1.0.0",
                PromptTemplateBodyChecksum = "sha256:seed",
                ProviderName = "test-provider",
                ModelName = "test-model",
                CreatedAt = now,
            };

            for (var index = 0; index < offered.Length; index++)
            {
                var change = offered[index];

                change.WorkspaceId = _fixture.WorkspaceA.Id;
                change.SortOrder = index;
                change.Disposition = AiChangeDisposition.Pending;

                proposal.Changes.Add(change);
            }

            db.AiProposals.Add(proposal);
        }

        await db.SaveChangesAsync(cancellation);

        return new SeededProposal(
            recipeId,
            detail.GetProperty("concurrencyToken").GetString()!,
            requestId,
            proposalId,
            [.. offered.Select(change => change.Id)],
            detail);
    }

    /// <summary>The two header changes most of this file dispositions: a headnote and a title.</summary>
    private static AiStructuredChange[] DefaultChanges(JsonElement detail) =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            ChangeKind = AiChangeKind.Set,
            TargetKind = AiChangeTargetKind.Recipe,
            FieldName = "headnote",
            BeforeValue = "The one my grandmother made.",
            AfterValue = "A warmer opening.",
        },
        new()
        {
            Id = Guid.NewGuid(),
            ChangeKind = AiChangeKind.Set,
            TargetKind = AiChangeTargetKind.Recipe,
            FieldName = "title",
            BeforeValue = "Olive oil cake",
            AfterValue = "Plum and olive oil cake",
        },
    ];

    /// <summary>The id of the recipe's one instruction step, from the detail the API returned.</summary>
    private static Guid StepIdOf(JsonElement detail) =>
        detail.GetProperty("instructionGroups").EnumerateArray().Single()
            .GetProperty("steps").EnumerateArray().Single()
            .GetProperty("id").GetGuid();

    private string Recipes() => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes";

    private async Task<List<AiStructuredChange>> ChangesAsync(Guid proposalId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AiStructuredChanges
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(change => change.AiProposalId == proposalId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AiProposalFeedback>> FeedbackAsync(Guid proposalId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AiProposalFeedback
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(feedback => feedback.AiProposalId == proposalId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AiOperation> OperationAsync(Guid requestId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AiOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(operation => operation.Id == requestId, TestContext.Current.CancellationToken);
    }
}
