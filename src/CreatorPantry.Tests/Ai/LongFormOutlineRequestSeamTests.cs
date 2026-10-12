using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Content;
using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// LF.2.2's outline request seam with nothing faked but the model and the clock, for two workspaces: what a
/// request queues and what it starts, what a spent allowance refuses, what the worker does with the proposal it
/// stores, and what a neighbouring workspace can see of any of it.
/// </summary>
/// <remarks>
/// <para>
/// The real <see cref="IAiOperationWorker"/> with a scripted handler. The plan the scripted handler returns is
/// built by the capability's own <c>Translate</c> and the shared proposal assembler, so the rows that land are
/// the rows a real run stores — the recipe pins among them — while what a model writes stays
/// <c>LongFormOutlineAiTaskHandlerTests</c>'s concern.
/// </para>
/// <para>
/// The creative-context facade is a stub that answers from the same database under the same workspace filter:
/// the real one needs the recipe and media modules to edit a context, and this file needs it only to say
/// whether a context is this workspace's. The routes' own use of the real facade is
/// <c>LongFormDraftEndpointTests</c>'s to prove, through the gateway.
/// </para>
/// <para>
/// SQLite, so the row versions do not move (see <c>SqliteModelCustomizer</c>). Nothing here asserts on
/// concurrency; the endpoint tests do that against SQL Server.
/// </para>
/// </remarks>
public sealed class LongFormOutlineRequestSeamTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid MembershipB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private const string AccountA = "user-a";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly AiTaskOptions _tasks = new();
    private readonly ScriptedTaskHandler _handler = new();

    public LongFormOutlineRequestSeamTests()
    {
        _connection.Open();
        _tasks.Enabled.Add(AiTaskCatalog.LongFormOutline);

        _provider = new ServiceCollection()
            .AddLogging()
            .AddTenancy()
            .AddAudit()
            .AddApplicationTime()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(_tasks)
            .AddAiTaskGate()
            .AddAiUsageModule()
            .AddContentModule()

            // After the module, so this is the registration that resolves: see the remarks on the stub.
            .AddScoped<ICreativeContextFacade, ContextsFromTheDatabase>()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .AddScoped<IAiOperationWorker, AiOperationWorker>()
            .AddScoped<IAiRequestQuotaGate, AiRequestQuotaGate>()
            .AddScoped<IAiLongFormOutlineRequestBusiness, AiLongFormOutlineRequestBusiness>()
            .AddScoped<IAiLongFormOutlineRequestFacade, AiLongFormOutlineRequestFacade>()
            .AddScoped<IValidator<StartLongFormDraftViewModel>, StartLongFormDraftViewModelValidator>()
            .AddScoped<IValidator<RequestLongFormOutlineViewModel>, RequestLongFormOutlineViewModelValidator>()
            .AddKeyedSingleton<IAiTaskHandler>(AiTaskType.LongFormOutline, _handler)
            .AddKeyedScoped<IAiProposalLandingHandler, LongFormOutlineProposalLanding>(AiTaskType.LongFormOutline)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "b", CreatedAt = Now });

        // WorkspaceMembership.UserId is a real foreign key, and the worker resolves its workspace from the
        // requesting membership rather than from anything a test set up.
        db.Users.AddRange(
            new ApplicationUser
            {
                Id = AccountA, UserName = AccountA, NormalizedUserName = "USER-A",
                Email = "user-a@example.com", NormalizedEmail = "USER-A@EXAMPLE.COM",
                DisplayName = "User A", CreatedAt = Now,
            },
            new ApplicationUser
            {
                Id = "user-b", UserName = "user-b", NormalizedUserName = "USER-B",
                Email = "user-b@example.com", NormalizedEmail = "USER-B@EXAMPLE.COM",
                DisplayName = "User B", CreatedAt = Now,
            });

        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership
            {
                Id = MembershipA, WorkspaceId = WorkspaceA, UserId = AccountA,
                Role = WorkspaceRole.Editor, Status = WorkspaceMembershipStatus.Active, JoinedAt = Now,
            },
            new WorkspaceMembership
            {
                Id = MembershipB, WorkspaceId = WorkspaceB, UserId = "user-b",
                Role = WorkspaceRole.Editor, Status = WorkspaceMembershipStatus.Active, JoinedAt = Now,
            });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Starting a draft ----

    [Fact]
    public async Task Starting_a_draft_queues_one_outline_request_and_makes_one_draft_under_its_id()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        var outcome = await StartAsync(WorkspaceA, contextId, LongFormKind.NewsletterIssue, "key-1", targetWords: 450, wanted: [LongFormSectionRole.Greeting]);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        Assert.False(outcome.Replayed);

        var started = outcome.Result.Value!;
        Assert.Equal(started.Request.AiProposalRequestId, started.Draft.Id);
        Assert.Equal(LongFormDraftStatus.Draft, started.Draft.Status);
        Assert.Equal(LongFormKind.NewsletterIssue, started.Draft.Kind);
        Assert.Null(started.Draft.Latest);
        Assert.Null(started.Draft.PlanCheck);
        Assert.Equal(AiOperationStatus.Requested, started.Request.Status);

        var operation = await SingleOperationAsync(WorkspaceA);
        Assert.Equal(AiTaskType.LongFormOutline, operation.TaskType);
        Assert.Equal(AiOperationScope.NotApplicable, operation.Scope);
        Assert.Null(operation.RecipeId);
        Assert.Equal(MembershipA, operation.RequestedByMembershipId);

        // The inputs are the creator's question and nothing else — no draft id, so a retry compares equal.
        var inputs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;
        Assert.Equal(
            new[]
            {
                LongFormOutlineInputs.CreativeContextId, LongFormOutlineInputs.Kind,
                LongFormOutlineInputs.TargetWords, LongFormOutlineInputs.WantedRoles,
            }.Order(),
            inputs.Keys.Order());
        Assert.Equal("450", inputs[LongFormOutlineInputs.TargetWords]);
        Assert.Equal("Greeting", inputs[LongFormOutlineInputs.WantedRoles]);
    }

    [Fact]
    public async Task Retrying_with_the_same_key_returns_the_same_draft_and_starts_nothing_new()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        var first = await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1");
        var again = await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1");

        Assert.True(again.Replayed);
        Assert.Equal(first.Result.Value!.Draft.Id, again.Result.Value!.Draft.Id);
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.LongFormDrafts.CountAsync(Ct)));
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(Ct)));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("context")]
    [InlineData("target")]
    [InlineData("wanted")]
    public async Task The_same_key_for_a_different_request_is_refused_and_starts_nothing(string changed)
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var otherContext = await SeedContextAsync(WorkspaceA);
        await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1");

        var different = changed switch
        {
            "kind" => await StartAsync(WorkspaceA, contextId, LongFormKind.NewsletterIssue, "key-1"),
            "context" => await StartAsync(WorkspaceA, otherContext, LongFormKind.BlogArticle, "key-1"),
            "target" => await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1", targetWords: 500),
            _ => await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1", wanted: [LongFormSectionRole.Faq]),
        };

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, different.Result.Error!.Code);
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.LongFormDrafts.CountAsync(Ct)));
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_context_that_is_unknown_and_one_that_is_another_workspaces_get_one_answer_and_start_nothing()
    {
        var theirs = await SeedContextAsync(WorkspaceB);

        var foreign = await StartAsync(WorkspaceA, theirs, LongFormKind.BlogArticle, "key-1");
        var unknown = await StartAsync(WorkspaceA, Guid.NewGuid(), LongFormKind.BlogArticle, "key-2");

        Assert.Equal(ContentErrorCodes.LongFormContextUnprocessable, foreign.Result.Error!.Code);
        Assert.Equal(foreign.Result.Error.Code, unknown.Result.Error!.Code);
        Assert.Equal(foreign.Result.Error.Message, unknown.Result.Error.Message);

        foreach (var workspaceId in new[] { WorkspaceA, WorkspaceB })
        {
            Assert.Equal(0, await CountAsync(workspaceId, db => db.LongFormDrafts.CountAsync(Ct)));
            Assert.Equal(0, await CountAsync(workspaceId, db => db.AiOperations.CountAsync(Ct)));
        }
    }

    [Theory]
    [InlineData(LongFormKind.BlogArticle, LongFormSectionRole.Greeting, 1)]
    [InlineData(LongFormKind.NewsletterIssue, LongFormSectionRole.Hook, 1)]
    [InlineData(LongFormKind.BlogArticle, LongFormSectionRole.Hook, 2)]
    public async Task A_section_this_kind_cannot_have_is_a_bad_request_before_anything_is_queued(
        LongFormKind kind, LongFormSectionRole role, int times)
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        var outcome = await StartAsync(WorkspaceA, contextId, kind, "key-1", wanted: [.. Enumerable.Repeat(role, times)]);

        Assert.Equal(AiLongFormOutlineRequestErrors.RequestInvalid, outcome.Result.Error!.Code);
        Assert.True(outcome.Result.Error.FieldErrors.ContainsKey("wantedRoles"));
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_request_with_no_key_or_a_length_out_of_range_is_refused()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        Assert.Equal(
            IdempotencyPolicy.KeyRequiredCode,
            (await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, idempotencyKey: null)).Result.Error!.Code);

        foreach (var words in new[] { AiPolicy.LongFormTargetWordsMin - 1, AiPolicy.LongFormTargetWordsMax + 1 })
        {
            var outcome = await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1", targetWords: words);

            Assert.Equal(AiLongFormOutlineRequestErrors.RequestInvalid, outcome.Result.Error!.Code);
            Assert.True(outcome.Result.Error.FieldErrors.ContainsKey("targetWords"));
        }

        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_task_that_is_switched_off_starts_nothing()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        _tasks.Enabled.Remove(AiTaskCatalog.LongFormOutline);

        var outcome = await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1");

        Assert.Equal(AiLongFormOutlineRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.LongFormDrafts.CountAsync(Ct)));
    }

    // ---- The allowance ----

    [Theory]
    [InlineData(false, AiProposalErrors.QuotaExhausted)]
    [InlineData(true, AiProposalErrors.QuotaSuspended)]
    public async Task A_spent_or_suspended_allowance_is_refused_at_admission_and_neither_queues_nor_starts_anything(
        bool suspended, string expected)
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        await GiveQuotaAsync(allowance: suspended ? 1000m : 0m, suspended);

        // A handler that would fail the test if the worker ever reached it.
        _handler.Behavior = (_, _) => throw new InvalidOperationException("The provider was reached.");

        var outcome = await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1");

        Assert.Equal(expected, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(Ct)));
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.LongFormDrafts.CountAsync(Ct)));
        Assert.Equal(0, (await RunPendingAsync()).Claimed);
    }

    // ---- Landing ----

    [Fact]
    public async Task A_generated_plan_lands_as_the_drafts_first_revision_pinned_to_every_recipe_it_was_written_against()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var bread = await SeedRecipeReferenceAsync(WorkspaceA, contextId, 0);
        var cake = await SeedRecipeReferenceAsync(WorkspaceA, contextId, 1);
        var started = (await StartAsync(WorkspaceA, contextId, LongFormKind.NewsletterIssue, "key-1")).Result.Value!;

        _handler.Behavior = (context, _) => Task.FromResult(PlanProposal(
            context,
            contextId,
            [bread, cake],
            ("Greeting", "Hello again", "Say hello.", null),
            ("Item", "A cake that keeps its crumb", "Introduce the cake.", 2),
            ("Item", "A loaf with no kneading", "Introduce the bread.", 1),
            ("Signoff", "Until next week", "Sign off.", null)));

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Proposed);

        var status = await StatusAsync(WorkspaceA, started.Draft.Id, started.Request.AiProposalRequestId);
        Assert.Equal(AiOperationStatus.Proposed, status.Request.Status);

        var draft = status.Draft;
        Assert.Equal(LongFormDraftStatus.Draft, draft.Status);
        Assert.Equal(1, draft.Latest!.RevisionNumber);
        Assert.Equal(ContentRevisionSource.AiGenerated, draft.Latest.Source);
        Assert.Equal(
            ["Hello again", "A cake that keeps its crumb", "A loaf with no kneading", "Until next week"],
            draft.Latest.Sections.Select(section => section.Heading));
        Assert.Equal(
            [null, cake.ReferenceId, bread.ReferenceId, null],
            draft.Latest.Sections.Select(section => section.SourceReferenceId));
        Assert.All(draft.Latest.Sections, section =>
        {
            Assert.False(section.IsWritten);
            Assert.False(section.Settled);
        });
        Assert.True(draft.PlanCheck!.IsValid);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var revision = await db.LongFormRevisions.AsNoTracking().SingleAsync(Ct);

        // What the plan was written with and against, all taken from the proposal.
        Assert.NotNull(revision.AiProposalId);
        Assert.Equal(AiTaskCatalog.LongFormOutline, revision.PromptTemplateId);
        Assert.Equal("1.0.0", revision.PromptTemplateVersion);
        Assert.Equal("AAAAAAAAB9E=", revision.CreativeContextVersion);
        Assert.Equal(MembershipA, revision.CreatedByMembershipId);

        var pins = await db.LongFormRevisionRecipePins.AsNoTracking().OrderBy(pin => pin.SortOrder).ToListAsync(Ct);
        Assert.Equal(
            new[] { (bread.ReferenceId, bread.RecipeId, bread.VersionId), (cake.ReferenceId, cake.RecipeId, cake.VersionId) },
            pins.Select(pin => (pin.CreativeContextReferenceId, pin.RecipeId, pin.RecipeVersionId)));
    }

    /// <summary>
    /// The sweep lands a recently stored proposal again, so a worker that died between storing one and landing
    /// it heals itself. Landing twice therefore has to be indistinguishable from landing once.
    /// </summary>
    [Fact]
    public async Task Landing_the_same_proposal_again_writes_no_second_revision()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var started = (await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1")).Result.Value!;
        _handler.Behavior = (context, _) => Task.FromResult(PlanProposal(context, contextId, [], Article));

        await RunPendingAsync();
        var maintenance = await RunMaintenanceAsync();

        Assert.Equal(1, maintenance.Landed);
        Assert.Equal(1, (await DraftAsync(WorkspaceA, started.Draft.Id)).Latest!.RevisionNumber);
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    /// <summary>
    /// The request is queued before the draft is made, so a fast worker — or a request that died between the
    /// two — can land a plan on a draft that does not exist yet. The plan names it, and makes it.
    /// </summary>
    [Fact]
    public async Task A_plan_that_lands_before_its_draft_exists_creates_the_draft_it_names()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var started = (await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1")).Result.Value!;

        // As if the request had died after queueing: the operation is there and the draft is not.
        await CountAsync(WorkspaceA, async db =>
        {
            db.LongFormDrafts.Remove(await db.LongFormDrafts.SingleAsync(Ct));

            return await db.SaveChangesAsync(Ct);
        });

        _handler.Behavior = (context, _) => Task.FromResult(PlanProposal(context, contextId, [], Article));
        await RunPendingAsync();

        var draft = await DraftAsync(WorkspaceA, started.Request.AiProposalRequestId);
        Assert.Equal(contextId, draft.CreativeContextId);
        Assert.Equal(LongFormKind.BlogArticle, draft.Kind);
        Assert.Equal(1, draft.Latest!.RevisionNumber);

        // And the retry the client would send next finds that draft rather than starting another.
        var retried = await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1");
        Assert.True(retried.Replayed);
        Assert.Equal(draft.Id, retried.Result.Value!.Draft.Id);
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.LongFormDrafts.CountAsync(Ct)));
    }

    /// <summary>
    /// A cancelled run is recorded as cancelled — not as a provider fault, and not as a plan nobody decided —
    /// and nothing is written for the creator to mistake for one.
    /// </summary>
    [Fact]
    public async Task A_cancelled_run_is_recorded_as_cancelled_and_leaves_the_draft_with_no_plan()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var started = (await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1")).Result.Value!;
        _handler.Behavior = (_, _) => Task.FromResult(AiTaskHandlerOutcome.ForFailure(
            AiFailureCategory.Cancelled, "The run was cancelled.", []));

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Failed);

        var status = await StatusAsync(WorkspaceA, started.Draft.Id, started.Request.AiProposalRequestId);
        Assert.Equal(AiOperationStatus.Failed, status.Request.Status);
        Assert.Equal(AiFailureCategory.Cancelled, status.Request.FailureCategory);
        Assert.Null(status.Request.Proposal);
        Assert.Equal(LongFormDraftStatus.Draft, status.Draft.Status);
        Assert.Null(status.Draft.Latest);
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    // ---- Asking again ----

    [Fact]
    public async Task Asking_again_lands_the_next_revision_on_the_same_draft_and_un_approves_the_plan()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var started = (await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1")).Result.Value!;
        _handler.Behavior = (context, _) => Task.FromResult(PlanProposal(context, contextId, [], Article));
        await RunPendingAsync();

        var landed = await DraftAsync(WorkspaceA, started.Draft.Id);
        var approved = await DraftsAsync(WorkspaceA, facade => facade.ApprovePlanAsync(
            AccountA,
            landed.Id,
            new ApproveLongFormPlanViewModel { ExpectedConcurrencyToken = landed.ConcurrencyToken, RevisionNumber = 1 },
            Ct));
        Assert.Equal(LongFormDraftStatus.Outlined, approved.Status);

        var again = await AgainAsync(WorkspaceA, started.Draft.Id, "key-2");

        Assert.True(again.Result.Succeeded, again.Result.Error?.Message);
        Assert.Equal(started.Draft.Id, again.Result.Value!.Draft.Id);
        Assert.NotEqual(started.Request.AiProposalRequestId, again.Result.Value.Request.AiProposalRequestId);

        _handler.Behavior = (context, _) => Task.FromResult(PlanProposal(
            context, contextId, [], ("Hook", "A second way in", "Open from another angle.", null)));
        await RunPendingAsync();

        var status = await StatusAsync(WorkspaceA, started.Draft.Id, again.Result.Value.Request.AiProposalRequestId);
        Assert.Equal(2, status.Draft.Latest!.RevisionNumber);
        Assert.Equal("A second way in", Assert.Single(status.Draft.Latest.Sections).Heading);

        // A new plan is a plan nobody has approved. The first revision is still the revision it was.
        Assert.Equal(LongFormDraftStatus.Draft, status.Draft.Status);
        Assert.Equal(2, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.LongFormDrafts.CountAsync(Ct)));

        // The first request still reads as the first request, and is not this draft's second.
        Assert.Equal(
            AiOperationStatus.Proposed,
            (await StatusAsync(WorkspaceA, started.Draft.Id, started.Request.AiProposalRequestId)).Request.Status);
    }

    [Theory]
    [InlineData(LongFormDraftStatus.Writing)]
    [InlineData(LongFormDraftStatus.Assembled)]
    public async Task A_draft_whose_sections_are_being_written_takes_no_new_plan(LongFormDraftStatus status)
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var started = (await StartAsync(WorkspaceA, contextId, LongFormKind.BlogArticle, "key-1")).Result.Value!;
        _handler.Behavior = (context, _) => Task.FromResult(PlanProposal(context, contextId, [], Article));

        await CountAsync(WorkspaceA, async db =>
        {
            (await db.LongFormDrafts.SingleAsync(Ct)).Status = status;

            return await db.SaveChangesAsync(Ct);
        });

        // Refused before the allowance is touched: nothing is queued for a plan that could not land.
        var again = await AgainAsync(WorkspaceA, started.Draft.Id, "key-2");
        Assert.Equal(ContentErrorCodes.LongFormPlanLocked, again.Result.Error!.Code);
        Assert.Equal(1, await CountAsync(WorkspaceA, db => db.AiOperations.CountAsync(Ct)));

        // And the request that was already in flight when writing began lands nothing.
        await RunPendingAsync();
        Assert.Equal(0, await CountAsync(WorkspaceA, db => db.LongFormRevisions.CountAsync(Ct)));
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task One_workspace_can_neither_poll_ask_again_for_nor_see_the_plan_of_anothers_draft()
    {
        var contextA = await SeedContextAsync(WorkspaceA);
        var contextB = await SeedContextAsync(WorkspaceB);
        var a = (await StartAsync(WorkspaceA, contextA, LongFormKind.BlogArticle, "key-1")).Result.Value!;
        var b = (await StartAsync(WorkspaceB, contextB, LongFormKind.BlogArticle, "key-1")).Result.Value!;

        _handler.Behavior = (context, _) => Task.FromResult(PlanProposal(
            context,
            context.WorkspaceId == WorkspaceA ? contextA : contextB,
            [],
            ("Hook", context.WorkspaceId == WorkspaceA ? "A's secret heading" : "B's own heading", "Open.", null)));

        Assert.Equal(2, (await RunPendingAsync()).Proposed);

        // Each plan landed on its own draft, in its own workspace, and nowhere else.
        Assert.Equal("A's secret heading", (await DraftAsync(WorkspaceA, a.Draft.Id)).Latest!.Sections.Single().Heading);
        Assert.Equal("B's own heading", (await DraftAsync(WorkspaceB, b.Draft.Id)).Latest!.Sections.Single().Heading);
        Assert.Equal(1, await CountAsync(WorkspaceB, db => db.LongFormRevisions.CountAsync(Ct)));

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceB);
        var outlines = scope.ServiceProvider.GetRequiredService<IAiLongFormOutlineRequestFacade>();
        var drafts = scope.ServiceProvider.GetRequiredService<ILongFormDraftFacade>();

        // B, asking by A's ids: nothing is there to be asked about.
        Assert.Equal(
            AiLongFormOutlineRequestErrors.RequestNotFound,
            (await outlines.GetAsync(a.Draft.Id, a.Request.AiProposalRequestId, Ct)).Error!.Code);
        Assert.Equal(ContentErrorCodes.LongFormDraftNotFound, (await drafts.GetAsync(a.Draft.Id, Ct)).Error!.Code);
        Assert.Equal(
            ContentErrorCodes.LongFormDraftNotFound,
            (await outlines.RequestAgainAsync(a.Draft.Id, new RequestLongFormOutlineViewModel(), "key-9", Ct)).Result.Error!.Code);

        // B's own draft cannot be made to answer for A's request, nor A's request for B's draft.
        Assert.Equal(
            AiLongFormOutlineRequestErrors.RequestNotFound,
            (await outlines.GetAsync(b.Draft.Id, a.Request.AiProposalRequestId, Ct)).Error!.Code);

        var listed = await drafts.ListRecentAsync(new LongFormDraftListViewModel(), Ct);
        Assert.Equal(b.Draft.Id, Assert.Single(listed.Value!.Items).Id);

        // The same key in two workspaces is two requests: neither replayed the other.
        Assert.NotEqual(a.Draft.Id, b.Draft.Id);
    }

    [Fact]
    public async Task A_plan_cannot_be_landed_on_another_workspaces_draft_or_against_another_workspaces_recipe()
    {
        var contextA = await SeedContextAsync(WorkspaceA);
        var contextB = await SeedContextAsync(WorkspaceB);
        var recipeA = await SeedRecipeReferenceAsync(WorkspaceA, contextA, 0);
        var a = (await StartAsync(WorkspaceA, contextA, LongFormKind.BlogArticle, "key-1")).Result.Value!;
        var b = (await StartAsync(WorkspaceB, contextB, LongFormKind.BlogArticle, "key-1")).Result.Value!;

        // B's worker run, returning a proposal that names A's recipe as its source: the defect this guards
        // against is a handler that was handed a neighbour's package.
        _handler.Behavior = (context, _) => Task.FromResult(context.WorkspaceId == WorkspaceB
            ? PlanProposal(context, contextB, [recipeA], Article)
            : AiTaskHandlerOutcome.ForFailure(AiFailureCategory.Cancelled, "Not this one.", []));

        await RunPendingAsync();

        // The pin's key is workspace-paired, so the revision is refused whole: B's draft has no plan, and
        // nothing of A's was written anywhere.
        Assert.Null((await DraftAsync(WorkspaceB, b.Draft.Id)).Latest);
        Assert.Equal(0, await CountAsync(WorkspaceB, db => db.LongFormRevisionRecipePins.CountAsync(Ct)));
        Assert.Null((await DraftAsync(WorkspaceA, a.Draft.Id)).Latest);

        // And B cannot name A's draft to land on. Asked through B's facade it is a draft B does not have — so B
        // tries to make one under that id, and the id is taken. With A's context that is refused before any
        // write; with B's own context it is refused by the key, and answered in the same words. Neither
        // answer says the id exists, and neither writes a row.
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceB);
        var drafts = scope.ServiceProvider.GetRequiredService<ILongFormDraftFacade>();

        foreach (var context in new[] { contextA, contextB })
        {
            var refused = await drafts.RecordGeneratedPlanAsync(
                new LongFormGeneratedPlanInput(
                    a.Draft.Id, context, LongFormKind.BlogArticle, Guid.NewGuid(), AiTaskCatalog.LongFormOutline, "1.0.0",
                    "sha256:" + new string('b', 64), null, null,
                    [new LongFormGeneratedSectionInput(LongFormSectionRole.Hook, "Planted", "Planted.", null)],
                    []),
                Ct);

            Assert.Equal(ContentErrorCodes.LongFormContextUnprocessable, refused.Error!.Code);
        }

        // An id nobody has, with a context B does not have, reads exactly the same.
        var unknown = await drafts.EnsureStartedAsync(Guid.NewGuid(), Guid.NewGuid(), LongFormKind.BlogArticle, Ct);
        Assert.Equal(ContentErrorCodes.LongFormContextUnprocessable, unknown.Error!.Code);

        Assert.Equal(1, await CountAsync(WorkspaceB, db => db.LongFormDrafts.CountAsync(Ct)));
        Assert.Equal(0, await CountAsync(WorkspaceB, db => db.LongFormRevisions.CountAsync(Ct)));
        Assert.Null((await DraftAsync(WorkspaceA, a.Draft.Id)).Latest);
    }

    // ---- Helpers ----

    private static readonly (string Role, string Heading, string Intent, int? Source)[] Article =
    [
        ("Hook", "The loaf for people who do not bake", "Open on how little this bread asks of the cook.", null),
        ("Closing", "Bake it this weekend", "Send the reader off to try it.", null),
    ];

    private sealed record SeededRecipe(Guid ReferenceId, Guid RecipeId, Guid VersionId);

    private async Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> StartAsync(
        Guid workspaceId,
        Guid contextId,
        LongFormKind kind,
        string? idempotencyKey,
        int? targetWords = null,
        IReadOnlyList<LongFormSectionRole>? wanted = null)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiLongFormOutlineRequestFacade>().StartAsync(
            new StartLongFormDraftViewModel
            {
                CreativeContextId = contextId,
                Kind = kind,
                TargetWords = targetWords,
                WantedRoles = wanted,
            },
            idempotencyKey,
            Ct);
    }

    private async Task<IdempotentOutcome<StartedLongFormDraftServiceModel>> AgainAsync(
        Guid workspaceId, Guid draftId, string idempotencyKey)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiLongFormOutlineRequestFacade>()
            .RequestAgainAsync(draftId, new RequestLongFormOutlineViewModel(), idempotencyKey, Ct);
    }

    private async Task<LongFormOutlineRequestStatusServiceModel> StatusAsync(Guid workspaceId, Guid draftId, Guid requestId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var result = await scope.ServiceProvider.GetRequiredService<IAiLongFormOutlineRequestFacade>()
            .GetAsync(draftId, requestId, Ct);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private Task<LongFormDraftServiceModel> DraftAsync(Guid workspaceId, Guid draftId) =>
        DraftsAsync(workspaceId, facade => facade.GetAsync(draftId, Ct));

    private async Task<LongFormDraftServiceModel> DraftsAsync(
        Guid workspaceId, Func<ILongFormDraftFacade, Task<OperationResult<LongFormDraftServiceModel>>> ask)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var result = await ask(scope.ServiceProvider.GetRequiredService<ILongFormDraftFacade>());

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<AiOperationWorkerPassSummary> RunPendingAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunPendingAsync(Ct);
    }

    private async Task<AiOperationMaintenanceSummary> RunMaintenanceAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunMaintenanceAsync(Ct);
    }

    /// <summary>
    /// A plan as the capability stores it: its own <c>Translate</c> over a validated answer, and the shared
    /// assembler, so the section rows and the recipe pins are exactly what a real run produces.
    /// </summary>
    private static AiTaskHandlerOutcome PlanProposal(
        AiTaskExecutionContext context,
        Guid contextId,
        IReadOnlyList<SeededRecipe> recipes,
        params (string Role, string Heading, string Intent, int? Source)[] sections)
    {
        var package = new CreativeContextPackage(
            context.WorkspaceId,
            AiTaskType.LongFormOutline,
            contextId,
            ContextVersion: "AAAAAAAAB9E=",
            new CreativeContextWords("Soda bread, autumn", PictureBrief: null),
            Channels: [],
            Day: null,
            [
                .. recipes.Select(recipe => new CreativeContextRecipeEntry(
                    recipe.ReferenceId, recipe.RecipeId, recipe.VersionId, 1, "Soda bread", null, null, null, null, null,
                    [], [], 0, 0, recipe.VersionId)),
            ],
            Concepts: [],
            Pictures: [],
            Prompts: [],
            Dropped: [],
            Omissions: [],
            EstimatedTokens: 120,
            Checksum: "sha256:" + new string('c', 64),
            AssembledAt: Now);

        var document = new AiLongFormOutlineOutputDocument
        {
            SchemaVersion = "content.long-form-outline.v1",
            Sections =
            [
                .. sections.Select(section => new AiLongFormOutlineSection
                {
                    Role = section.Role,
                    Heading = section.Heading,
                    Intent = section.Intent,
                    Source = section.Source,
                }),
            ],
        };

        var (changes, warnings) = LongFormOutlineAiTaskHandler.Translate(document, package);
        var pinned = recipes.Count > 0 ? recipes[0].VersionId : (Guid?)null;

        var assembly = AiProposalAssembler.Assemble(
            context.WorkspaceId,
            context.OperationId,
            pinnedVersionId: pinned,
            currentVersionId: pinned,
            new AiOutputDocument { SchemaVersion = document.SchemaVersion, Warnings = warnings },
            changes,
            new AiProposalProvenance(
                document.SchemaVersion,
                AiTaskCatalog.LongFormOutline,
                "1.0.0",
                "sha256:" + new string('b', 64),
                "test-provider",
                "test-model",
                null),
            Now,
            null,
            package);

        Assert.True(assembly.Succeeded, assembly.Failure?.Message);

        return AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, [Attempt()]);
    }

    private static AiAttemptRecord Attempt() => new()
    {
        AttemptNumber = 1,
        ProviderName = "test-provider",
        ModelName = "test-model",
        PromptTemplateId = AiTaskCatalog.LongFormOutline,
        PromptTemplateVersion = "1.0.0",
        StartedAt = Now,
        CompletedAt = Now,
        LatencyMilliseconds = 10,
        InputTokens = 100,
        CorrelationId = Guid.NewGuid(),
    };

    private async Task<Guid> SeedContextAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var context = CreativeContextSeeds.NewContext(Now);
        db.CreativeContexts.Add(context);
        await db.SaveChangesAsync(Ct);

        return context.Id;
    }

    /// <summary>A recipe with one version, and a reference to it on the context.</summary>
    private async Task<SeededRecipe> SeedRecipeReferenceAsync(Guid workspaceId, Guid contextId, int sortOrder)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var (recipeId, versionId) = await CreativeContextSeeds.RecipeAsync(db, Now, Ct);

        var reference = new CreativeContextReference
        {
            Id = Guid.NewGuid(),
            CreativeContextId = contextId,
            Kind = CreativeContextReferenceKind.Recipe,
            SortOrder = sortOrder,
            RecipeId = recipeId,
            RecipeVersionId = versionId,
            AddedAt = Now,
        };
        db.CreativeContextReferences.Add(reference);
        await db.SaveChangesAsync(Ct);

        return new SeededRecipe(reference.Id, recipeId, versionId);
    }

    private async Task GiveQuotaAsync(decimal allowance, bool suspended)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = AccountA,
            Unit = AiQuotaUnit.Credits,
            Allowance = allowance,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = suspended,
            EffectiveFrom = Now.AddDays(-30),
            LastChangedAt = Now.AddDays(-30),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> CountAsync(Guid workspaceId, Func<CreatorPantryDbContext, Task<int>> count)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await count(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private async Task<AiOperation> SingleOperationAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AiOperations.AsNoTracking().SingleAsync(Ct);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "a" : "b",
            workspaceId == WorkspaceA ? MembershipA : MembershipB,
            WorkspaceRole.Editor,
            workspaceId == WorkspaceA ? AccountA : "user-b");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    /// <summary>A handler whose behaviour is set per test; the capability itself is LF.2.1's own suite.</summary>
    private sealed class ScriptedTaskHandler : IAiTaskHandler
    {
        public Func<AiTaskExecutionContext, CancellationToken, Task<AiTaskHandlerOutcome>> Behavior { get; set; } =
            (_, _) => Task.FromResult(AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation, "No behaviour was scripted for this test.", []));

        public Task<AiTaskHandlerOutcome> HandleAsync(
            AiTaskExecutionContext context, CancellationToken cancellationToken) =>
            Behavior(context, cancellationToken);
    }

    /// <summary>
    /// The one question this seam asks of the creative-context facade — is this context ours — answered from
    /// the database under the resolved workspace's filter, as the real facade answers it.
    /// </summary>
    private sealed class ContextsFromTheDatabase(CreatorPantryDbContext db) : ICreativeContextFacade
    {
        public async Task<OperationResult<CreativeContextServiceModel>> GetAsync(Guid contextId, CancellationToken cancellationToken) =>
            await db.CreativeContexts.AsNoTracking().AnyAsync(context => context.Id == contextId, cancellationToken)
                ? OperationResult<CreativeContextServiceModel>.Success(null!)
                : OperationResult<CreativeContextServiceModel>.Failure(new OperationError(
                    ContentErrorCodes.CreativeContextNotFound,
                    "That creative context could not be found.",
                    new Dictionary<string, string[]>()));

        public Task<IdempotentOutcome<CreativeContextServiceModel>> CreateAsync(
            string actorUserId, CreateCreativeContextViewModel model, string? idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<CreativeContextPictureServiceModel>>> ListPicturesAsync(
            Guid contextId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<CreativeContextSummaryServiceModel>>> ListRecentAsync(
            CreativeContextListViewModel model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult<CreativeContextServiceModel>> PatchAsync(
            string actorUserId, Guid contextId, PatchCreativeContextViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<CreativeContextServiceModel>> AddReferenceAsync(
            Guid contextId, AddCreativeContextReferenceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<CreativeContextServiceModel>> RemoveReferenceAsync(
            Guid contextId, Guid referenceId, string? expectedConcurrencyToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
