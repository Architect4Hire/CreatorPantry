using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Time;
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
/// AF.6.4's request seam end to end over a real queue: what a client may ask for, what an idempotency key
/// names, what a spent allowance refuses, what the worker does with the proposal it stores, and what a
/// neighbouring workspace can see of any of it.
/// </summary>
/// <remarks>
/// <para>
/// The real <see cref="IAiOperationWorker"/> with a scripted handler, rather than the channel-posts capability
/// itself: what the model writes is AF.6.3's concern and <c>ChannelPostsAiTaskHandlerTests</c> covers it. What
/// this file covers is the seam around it — the queued row, the landing step that turns a stored proposal into
/// post revisions, and the answers a creator gets.
/// </para>
/// <para>
/// SQLite, so the row versions do not move (see <c>SqliteModelCustomizer</c>). Nothing here asserts on
/// concurrency; <c>SocialPackageTests</c> does that against a real SQL Server.
/// </para>
/// </remarks>
public sealed class ChannelPostRequestSeamTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid MembershipB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private const string Caption = "Olive oil cake, still warm. Recipe on the blog.";
    private const string PinDescription = "Soda bread, torn open on linen.";
    private const string AccountA = "user-a";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly AiTaskOptions _tasks = new();
    private readonly ScriptedTaskHandler _handler = new();

    public ChannelPostRequestSeamTests()
    {
        _connection.Open();
        _tasks.Enabled.Add(AiTaskCatalog.ChannelPosts);

        _provider = new ServiceCollection()
            .AddLogging()
            .AddTenancy()
            .AddAudit()
            .AddApplicationTime()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(_tasks)
            .AddAiUsageModule()
            .AddContentModule()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .AddScoped<IAiOperationWorker, AiOperationWorker>()
            .AddScoped<IAiRequestQuotaGate, AiRequestQuotaGate>()
            .AddScoped<IAiChannelPostsRequestBusiness, AiChannelPostsRequestBusiness>()
            .AddScoped<IAiChannelPostsRequestFacade, AiChannelPostsRequestFacade>()
            .AddScoped<IValidator<RequestChannelPostsViewModel>, RequestChannelPostsViewModelValidator>()
            .AddKeyedSingleton<IAiTaskHandler>(AiTaskType.ChannelPosts, _handler)
            .AddKeyedScoped<IAiProposalLandingHandler, ChannelPostsProposalLanding>(AiTaskType.ChannelPosts)
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

    // ---- what a client may ask for -----------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_task_is_refused_before_anything_is_queued()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        _tasks.Enabled.Clear();

        var outcome = await RequestAsync(WorkspaceA, contextId, "key-1", "instagram");

        Assert.Equal(AiChannelPostsRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
    }

    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, contextId, idempotencyKey: null, "instagram");

        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
    }

    /// <param name="channels">
    /// The keys, pipe-separated — none, the same one twice, one that names nothing, and nine of which two are
    /// the same key with a stray space.
    /// </param>
    [Theory]
    [InlineData("")]
    [InlineData("instagram|instagram")]
    [InlineData("not-a-channel")]
    [InlineData("blog|newsletter|instagram|tiktok|pinterest|facebook|x|threads|blog ")]
    public async Task A_request_that_does_not_name_a_writable_set_of_channels_is_refused(string channels)
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var channelKeys = channels.Length == 0 ? [] : channels.Split('|');

        var outcome = await RequestAsync(WorkspaceA, contextId, "key-1", channelKeys);

        Assert.Equal(AiChannelPostsRequestErrors.RequestInvalid, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
    }

    /// <summary>
    /// The queued row names no recipe and addresses no part of one, which is what the capability is: it writes
    /// copy about a piece of work and proposes no change to the dish.
    /// </summary>
    [Fact]
    public async Task A_request_queues_an_operation_with_no_recipe_and_a_not_applicable_scope()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, contextId, "key-1", "instagram", "pinterest");

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        Assert.False(outcome.Replayed);

        var operation = await SingleOperationAsync(WorkspaceA);
        Assert.Equal(AiTaskType.ChannelPosts, operation.TaskType);
        Assert.Equal(AiOperationScope.NotApplicable, operation.Scope);
        Assert.Equal(AiOperationStatus.Requested, operation.Status);
        Assert.Null(operation.RecipeId);
        Assert.Null(operation.RecipeVersionId);
        Assert.Equal(MembershipA, operation.RequestedByMembershipId);
    }

    /// <summary>The stored inputs are exactly what the handler reads back, and nothing else.</summary>
    [Fact]
    public async Task The_stored_inputs_are_the_piece_of_work_and_the_channels_in_order()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, contextId, "key-1", "pinterest", "instagram");

        var inputs = await InputsAsync(WorkspaceA);
        Assert.Equal(contextId, ChannelPostsInputs.ReadContextId(inputs));
        Assert.Equal(new[] { "pinterest", "instagram" }, ChannelPostsInputs.ReadChannelKeys(inputs));
        Assert.Equal(2, inputs!.Count);
    }

    // ---- what the key names ------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_the_same_request_replays_the_first_answer()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        var first = await RequestAsync(WorkspaceA, contextId, "key-1", "instagram");
        var again = await RequestAsync(WorkspaceA, contextId, "key-1", "instagram");

        Assert.True(again.Result.Succeeded, again.Result.Error?.Message);
        Assert.True(again.Replayed);
        Assert.Equal(first.Result.Value!.AiProposalRequestId, again.Result.Value!.AiProposalRequestId);
        Assert.Equal(1, await CountOperationsAsync(WorkspaceA));
    }

    [Fact]
    public async Task The_same_key_with_different_channels_is_refused()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, contextId, "key-1", "instagram");
        var different = await RequestAsync(WorkspaceA, contextId, "key-1", "instagram", "pinterest");

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, different.Result.Error!.Code);
        Assert.Equal(1, await CountOperationsAsync(WorkspaceA));
    }

    /// <summary>
    /// The order is part of the request, because the posts come back in it — so the same two channels the
    /// other way round is a different question, not a retry.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_the_same_channels_in_another_order_is_refused()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, contextId, "key-1", "instagram", "pinterest");
        var reordered = await RequestAsync(WorkspaceA, contextId, "key-1", "pinterest", "instagram");

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, reordered.Result.Error!.Code);
        Assert.Equal(1, await CountOperationsAsync(WorkspaceA));
    }

    [Fact]
    public async Task The_same_key_with_a_different_piece_of_work_is_refused()
    {
        var first = await SeedContextAsync(WorkspaceA);
        var second = await SeedContextAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, first, "key-1", "instagram");
        var elsewhere = await RequestAsync(WorkspaceA, second, "key-1", "instagram");

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, elsewhere.Result.Error!.Code);
        Assert.Equal(1, await CountOperationsAsync(WorkspaceA));
    }

    // ---- the allowance -----------------------------------------------------------------------------------

    /// <summary>
    /// Refused at admission: no operation is written, so no provider is ever reached. The route tells the
    /// creator what is spent and when it comes back (USAGE-007).
    /// </summary>
    [Fact]
    public async Task A_spent_allowance_is_refused_before_anything_is_queued()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        await GiveQuotaAsync(allowance: 0m);

        var outcome = await RequestAsync(WorkspaceA, contextId, "key-1", "instagram");

        Assert.Equal(AiProposalErrors.QuotaExhausted, outcome.Result.Error!.Code);
        Assert.False(outcome.Replayed);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
    }

    [Fact]
    public async Task A_suspended_account_is_refused_before_anything_is_queued()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        await GiveQuotaAsync(allowance: 1000m, suspended: true);

        var outcome = await RequestAsync(WorkspaceA, contextId, "key-1", "instagram");

        Assert.Equal(AiProposalErrors.QuotaSuspended, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
    }

    // ---- retired channels --------------------------------------------------------------------------------

    [Fact]
    public async Task A_retired_channel_cannot_be_written_for_the_first_time()
    {
        var contextId = await SeedContextAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, contextId, "key-1", [Retired], channels: WithRetiredTiktok);

        Assert.Equal(AiChannelPostsRequestErrors.ChannelRetired, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
    }

    /// <summary>
    /// Retiring a channel must not strand the posts already written for it: a slot that exists can be written
    /// again for ever, which is what makes "regenerate" work on a post whose channel the product has retired.
    /// </summary>
    [Fact]
    public async Task A_retired_channel_that_already_has_a_post_can_be_written_again()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        await SeedPostAsync(WorkspaceA, contextId, Retired);

        var outcome = await RequestAsync(
            WorkspaceA, contextId, "key-1", [Retired], channels: WithRetiredTiktok);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        Assert.Equal(1, await CountOperationsAsync(WorkspaceA));
    }

    // ---- isolation ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_piece_of_work_in_the_other_workspace_is_not_found()
    {
        var theirs = await SeedContextAsync(WorkspaceB);

        var outcome = await RequestAsync(WorkspaceA, theirs, "key-1", "instagram");

        Assert.Equal(AiChannelPostsRequestErrors.ContextNotFound, outcome.Result.Error!.Code);
        Assert.Equal(0, await CountOperationsAsync(WorkspaceA));
        Assert.Equal(0, await CountOperationsAsync(WorkspaceB));
    }

    [Fact]
    public async Task The_other_workspaces_request_is_not_found_on_the_status_read()
    {
        var theirs = await SeedContextAsync(WorkspaceB);
        var queued = await RequestAsync(WorkspaceB, theirs, "key-1", "instagram");
        var requestId = queued.Result.Value!.AiProposalRequestId;

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var read = await scope.ServiceProvider.GetRequiredService<IAiChannelPostsRequestFacade>()
            .GetAsync(requestId, Ct);

        Assert.Equal(AiChannelPostsRequestErrors.RequestNotFound, read.Error!.Code);
    }

    /// <summary>A request that ran some other task is not this route's resource, and reads as absent.</summary>
    [Fact]
    public async Task A_request_that_ran_a_different_task_is_not_found()
    {
        Guid otherTask;

        using (var scope = _provider.CreateScope())
        {
            Resolve(scope, WorkspaceA);
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            otherTask = await CreativeContextSeeds.ConceptRequestAsync(db, Now, Ct);
        }

        using var read = _provider.CreateScope();
        Resolve(read, WorkspaceA);
        var result = await read.ServiceProvider.GetRequiredService<IAiChannelPostsRequestFacade>()
            .GetAsync(otherTask, Ct);

        Assert.Equal(AiChannelPostsRequestErrors.RequestNotFound, result.Error!.Code);
    }

    // ---- the landing -------------------------------------------------------------------------------------

    /// <summary>
    /// The step after the proposal: every stored body becomes one revision of its channel, awaiting a
    /// decision, carrying the measurement and the provenance the proposal recorded.
    /// </summary>
    [Fact]
    public async Task A_stored_proposal_becomes_one_post_revision_per_channel()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var requestId = await QueueAsync(contextId, "instagram", "pinterest");
        _handler.Behavior = (context, _) => Task.FromResult(PostsProposal(
            context, contextId, [("instagram", Caption), ("pinterest", PinDescription)]));

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Proposed);

        var status = await StatusAsync(WorkspaceA, requestId);
        Assert.Equal(AiOperationStatus.Proposed, status.Request.Status);

        var package = status.Package!;
        Assert.Equal(contextId, package.CreativeContextId);
        Assert.Equal(new[] { "instagram", "pinterest" }, package.Channels.Select(channel => channel.ChannelKey).Order());

        var instagram = package.Channels.Single(channel => channel.ChannelKey == "instagram");
        Assert.Equal(ContentProposalStatus.Proposed, instagram.Status);
        Assert.Null(instagram.Accepted);
        Assert.Equal(1, instagram.Latest.RevisionNumber);
        Assert.Equal(ContentRevisionSource.AiGenerated, instagram.Latest.Source);
        Assert.Equal(Caption, instagram.Latest.Body);
        Assert.Equal(status.Request.Proposal!.ProposalId, instagram.Latest.AiProposalId);
        Assert.Equal(AiTaskCatalog.ChannelPosts, instagram.Latest.PromptTemplateId);
        Assert.Equal(SocialLimitStatus.Within, instagram.Latest.LimitStatus);
        Assert.Equal(Caption.Length, instagram.Latest.CharacterCount);
    }

    /// <summary>
    /// An over-limit body lands over its limit. The measurement is the handler's and this step copies it;
    /// nothing here shortens a creator's post to make a number look right.
    /// </summary>
    [Fact]
    public async Task An_over_limit_body_lands_as_written_and_flagged()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var requestId = await QueueAsync(contextId, "x");
        var body = new string('x', 300);

        _handler.Behavior = (context, _) => Task.FromResult(PostsProposal(
            context, contextId, [("x", body)], count: 300, limit: 280, over: true));

        await RunPendingAsync();

        var latest = (await StatusAsync(WorkspaceA, requestId)).Package!.Channels.Single().Latest;
        Assert.Equal(body, latest.Body);
        Assert.Equal(SocialLimitStatus.Over, latest.LimitStatus);
        Assert.Equal(300, latest.CharacterCount);
        Assert.Equal(280, latest.CharacterLimit);
    }

    /// <summary>
    /// The sweep re-lands a recently stored proposal, so a worker that died between storing one and landing it
    /// heals itself. Landing twice therefore has to be indistinguishable from landing once.
    /// </summary>
    [Fact]
    public async Task Landing_the_same_proposal_again_writes_no_second_revision()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var requestId = await QueueAsync(contextId, "instagram");
        _handler.Behavior = (context, _) => Task.FromResult(PostsProposal(
            context, contextId, [("instagram", Caption)]));

        await RunPendingAsync();
        var maintenance = await RunMaintenanceAsync();

        Assert.Equal(1, maintenance.Landed);

        var channel = (await StatusAsync(WorkspaceA, requestId)).Package!.Channels.Single();
        Assert.Equal(1, channel.Latest.RevisionNumber);
        Assert.Equal(1, await CountRevisionsAsync(WorkspaceA));
    }

    /// <summary>
    /// A cancelled run is recorded as cancelled — not as a provider fault, and not as a proposal nobody
    /// decided — and nothing is written for the creator to mistake for a post.
    /// </summary>
    [Fact]
    public async Task A_cancelled_run_is_recorded_as_cancelled_and_lands_no_posts()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var requestId = await QueueAsync(contextId, "instagram");
        _handler.Behavior = (_, _) => Task.FromResult(AiTaskHandlerOutcome.ForFailure(
            AiFailureCategory.Cancelled, "The run was cancelled.", []));

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Failed);

        var status = await StatusAsync(WorkspaceA, requestId);
        Assert.Equal(AiOperationStatus.Failed, status.Request.Status);
        Assert.Equal(AiFailureCategory.Cancelled, status.Request.FailureCategory);
        Assert.Null(status.Request.Proposal);
        Assert.Null(status.Package);
        Assert.Equal(0, await CountRevisionsAsync(WorkspaceA));
    }

    /// <summary>
    /// Two workspaces with work queued at once, run by one worker pass: each workspace's posts land in its own
    /// piece of work and nowhere else.
    /// </summary>
    /// <remarks>
    /// The claim step is the one cross-workspace read in the domain, and the landing that follows it is a
    /// write — so "the worker served two workspaces in one pass and kept them apart" is the property that
    /// matters here, not that either one worked on its own (tenancy.md).
    /// </remarks>
    [Fact]
    public async Task One_pass_serving_both_workspaces_lands_each_ones_posts_in_its_own_work()
    {
        var ours = await SeedContextAsync(WorkspaceA);
        var theirs = await SeedContextAsync(WorkspaceB);
        var ourRequest = await QueueAsync(WorkspaceA, ours, "instagram");
        var theirRequest = await QueueAsync(WorkspaceB, theirs, "pinterest");

        // One scripted handler for both, keyed off the workspace the worker resolved — so a post landing in
        // the wrong workspace would be this fixture's own doing rather than something it could not see.
        _handler.Behavior = (context, _) => Task.FromResult(context.WorkspaceId == WorkspaceA
            ? PostsProposal(context, ours, [("instagram", Caption)])
            : PostsProposal(context, theirs, [("pinterest", PinDescription)]));

        var summary = await RunPendingAsync();

        Assert.Equal(2, summary.Proposed);

        var ourPackage = (await StatusAsync(WorkspaceA, ourRequest)).Package!;
        var theirPackage = (await StatusAsync(WorkspaceB, theirRequest)).Package!;

        Assert.Equal(ours, ourPackage.CreativeContextId);
        Assert.Equal(theirs, theirPackage.CreativeContextId);
        Assert.Equal("instagram", ourPackage.Channels.Single().ChannelKey);
        Assert.Equal("pinterest", theirPackage.Channels.Single().ChannelKey);
        Assert.Equal(Caption, ourPackage.Channels.Single().Latest.Body);
        Assert.Equal(PinDescription, theirPackage.Channels.Single().Latest.Body);

        // One revision each, counted inside each workspace's own filter.
        Assert.Equal(1, await CountRevisionsAsync(WorkspaceA));
        Assert.Equal(1, await CountRevisionsAsync(WorkspaceB));

        // And the sweep, which reads across workspaces to find them, re-lands both without crossing either.
        var maintenance = await RunMaintenanceAsync();

        Assert.Equal(2, maintenance.Landed);
        Assert.Equal(1, await CountRevisionsAsync(WorkspaceA));
        Assert.Equal(1, await CountRevisionsAsync(WorkspaceB));
    }

    /// <summary>
    /// The sweep finds the proposal that has not landed even while older, already-landed ones are in the same
    /// window — the batch is ordered newest-first for exactly this case.
    /// </summary>
    [Fact]
    public async Task The_sweep_reaches_a_proposal_that_never_landed()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var requestId = await QueueAsync(WorkspaceA, contextId, "instagram");

        // A proposal stored with no landing: the handler succeeds and the worker commits it, with the landing
        // step unregistered for this pass. That is the crash this sweep exists to heal.
        _handler.Behavior = (context, _) => Task.FromResult(PostsProposal(
            context, contextId, [("instagram", Caption)]));

        await RunPendingWithoutLandingAsync();

        Assert.Null((await StatusAsync(WorkspaceA, requestId)).Package);

        var maintenance = await RunMaintenanceAsync();

        Assert.Equal(1, maintenance.Landed);
        Assert.Equal(Caption, (await StatusAsync(WorkspaceA, requestId)).Package!.Channels.Single().Latest.Body);
    }

    /// <summary>Polling before the posts exist is a status, not an absence.</summary>
    [Fact]
    public async Task A_request_nobody_has_run_yet_reads_as_requested_with_no_package()
    {
        var contextId = await SeedContextAsync(WorkspaceA);
        var requestId = await QueueAsync(contextId, "instagram");

        var status = await StatusAsync(WorkspaceA, requestId);

        Assert.Equal(AiOperationStatus.Requested, status.Request.Status);
        Assert.Null(status.Request.Proposal);
        Assert.Null(status.Package);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>A catalogue in which TikTok has been retired. Every other channel is the product's own.</summary>
    private const string Retired = "tiktok";

    private static IContentChannelCatalog WithRetiredTiktok { get; } = new ContentChannelCatalog(
        new ContentChannelCatalog().All.Select(channel =>
            channel.Key == Retired ? channel with { IsActive = false } : channel));

    private Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid workspaceId, Guid contextId, string? idempotencyKey, params string[] channelKeys) =>
        RequestAsync(workspaceId, contextId, idempotencyKey, channelKeys, channels: null);

    private async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid workspaceId,
        Guid contextId,
        string? idempotencyKey,
        string[] channelKeys,
        IContentChannelCatalog? channels)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var services = scope.ServiceProvider;
        var model = new RequestChannelPostsViewModel { CreativeContextId = contextId, ChannelKeys = channelKeys };

        // The business built by hand only when a test needs a catalogue the container does not have; the
        // facade is the real one either way, so the validator and the key rule are the shipped ones.
        var business = channels is null
            ? services.GetRequiredService<IAiChannelPostsRequestBusiness>()
            : new AiChannelPostsRequestBusiness(
                services.GetRequiredService<IAiOperationDataLayer>(),
                services.GetRequiredService<IAiRequestQuotaGate>(),
                services.GetRequiredService<ISocialPackageFacade>(),
                channels,
                services.GetRequiredService<IWorkspaceContext>(),
                _tasks,
                services.GetRequiredService<IClock>());

        var facade = new AiChannelPostsRequestFacade(
            business, services.GetRequiredService<IValidator<RequestChannelPostsViewModel>>());

        return await facade.RequestAsync(model, idempotencyKey, Ct);
    }

    /// <summary>A queued request, by the shipped seam, ready for the worker to claim.</summary>
    private Task<Guid> QueueAsync(Guid contextId, params string[] channelKeys) =>
        QueueAsync(WorkspaceA, contextId, channelKeys);

    /// <inheritdoc cref="QueueAsync(Guid, string[])"/>
    private async Task<Guid> QueueAsync(Guid workspaceId, Guid contextId, params string[] channelKeys)
    {
        var outcome = await RequestAsync(workspaceId, contextId, $"key-{Guid.NewGuid():N}", channelKeys);
        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        return outcome.Result.Value!.AiProposalRequestId;
    }

    private async Task<ChannelPostRequestStatusServiceModel> StatusAsync(Guid workspaceId, Guid requestId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var result = await scope.ServiceProvider.GetRequiredService<IAiChannelPostsRequestFacade>()
            .GetAsync(requestId, Ct);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    private async Task<AiOperationWorkerPassSummary> RunPendingAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunPendingAsync(Ct);
    }

    /// <summary>
    /// A worker pass with no landing step registered: the proposal commits and nothing follows it.
    /// </summary>
    /// <remarks>
    /// Its own container rather than a flag on the fixture's, because "there is no handler for this task" is
    /// what the worker actually sees when a pass dies before landing — the keyed service is simply absent.
    /// </remarks>
    private async Task RunPendingWithoutLandingAsync()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddTenancy()
            .AddAudit()
            .AddApplicationTime()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(_tasks)
            .AddAiUsageModule()
            .AddContentModule()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .AddScoped<IAiOperationWorker, AiOperationWorker>()
            .AddKeyedSingleton<IAiTaskHandler>(AiTaskType.ChannelPosts, _handler)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        await using (services)
        {
            using var scope = services.CreateScope();
            var summary = await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunPendingAsync(Ct);

            Assert.Equal(1, summary.Proposed);
        }
    }

    private async Task<AiOperationMaintenanceSummary> RunMaintenanceAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunMaintenanceAsync(Ct);
    }

    /// <summary>One post per channel as the handler stores them: a body row, and the server's own fields beside it.</summary>
    private static AiTaskHandlerOutcome PostsProposal(
        AiTaskExecutionContext context,
        Guid contextId,
        (string Channel, string Body)[] posts,
        int? count = null,
        int limit = 2200,
        bool over = false)
    {
        var proposal = new AiProposal
        {
            WorkspaceId = context.WorkspaceId,
            AiOperationId = context.OperationId,
            OutputSchemaVersion = "content.channel-posts.v1",
            PromptTemplateId = AiTaskCatalog.ChannelPosts,
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:" + new string('b', 64),
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = Now,
            CreativeContext = new AiProposalCreativeContext
            {
                Id = Guid.NewGuid(),
                WorkspaceId = context.WorkspaceId,
                CreativeContextId = contextId,
                ContextVersion = "AAAAAAAAB9E=",
                Checksum = "sha256:" + new string('c', 64),
                EstimatedTokens = 120,
                AssembledAt = Now,
            },
        };

        var sortOrder = 0;

        for (var index = 0; index < posts.Length; index++)
        {
            var (channel, body) = posts[index];
            var targetId = Guid.NewGuid();

            proposal.Changes.Add(new AiStructuredChange
            {
                Id = Guid.NewGuid(),
                WorkspaceId = context.WorkspaceId,
                ChangeKind = AiChangeKind.Add,
                TargetKind = AiChangeTargetKind.ChannelPost,
                TargetId = targetId,
                AfterValue = body,
                ProposedPosition = index,
                SortOrder = sortOrder++,
            });

            foreach (var (field, value) in new[]
            {
                (ChannelPostFields.ChannelKey, channel),
                (ChannelPostFields.CharacterCount, (count ?? body.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (ChannelPostFields.CharacterLimit, limit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (ChannelPostFields.LimitStatus, over ? ChannelPostFields.Over : ChannelPostFields.Within),
                (ChannelPostFields.ProfileVersion, "1.0.0"),
            })
            {
                proposal.Changes.Add(new AiStructuredChange
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = context.WorkspaceId,
                    ChangeKind = AiChangeKind.Set,
                    TargetKind = AiChangeTargetKind.ChannelPost,
                    TargetId = targetId,
                    FieldName = field,
                    AfterValue = value,
                    SortOrder = sortOrder++,
                });
            }
        }

        return AiTaskHandlerOutcome.ForProposal(proposal, [Attempt()]);
    }

    private static AiAttemptRecord Attempt() => new()
    {
        AttemptNumber = 1,
        ProviderName = "test-provider",
        ModelName = "test-model",
        PromptTemplateId = AiTaskCatalog.ChannelPosts,
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

    /// <summary>
    /// A post for one channel, seeded directly.
    /// </summary>
    /// <remarks>
    /// Directly rather than through the facade because the only test that needs it needs a post on a
    /// <em>retired</em> channel, and the write seam refuses to open a slot for one — which is the rule the
    /// test is about.
    /// </remarks>
    private async Task SeedPostAsync(Guid workspaceId, Guid contextId, string channelKey)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var package = new SocialPackage
        {
            Id = Guid.NewGuid(),
            CreativeContextId = contextId,
            CreatedByMembershipId = MembershipA,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        var channel = new SocialPackageChannel
        {
            Id = Guid.NewGuid(),
            SocialPackageId = package.Id,
            ChannelKey = channelKey,
            Status = ContentProposalStatus.Proposed,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        package.Channels.Add(channel);

        db.SocialPackages.Add(package);
        await db.SaveChangesAsync(Ct);

        db.SocialRevisions.Add(new SocialRevision
        {
            Id = Guid.NewGuid(),
            SocialPackageChannelId = channel.Id,
            RevisionNumber = 1,
            Source = ContentRevisionSource.CreatorEdit,
            Body = Caption,
            CreatedByMembershipId = MembershipA,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task GiveQuotaAsync(decimal allowance, bool suspended = false)
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

    private async Task<int> CountOperationsAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiOperations.CountAsync(candidate => candidate.TaskType == AiTaskType.ChannelPosts, Ct);
    }

    private async Task<AiOperation> SingleOperationAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiOperations.AsNoTracking().SingleAsync(Ct);
    }

    private async Task<IReadOnlyDictionary<string, string>?> InputsAsync(Guid workspaceId) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            (await SingleOperationAsync(workspaceId)).TaskInputsJson!);

    private async Task<int> CountRevisionsAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.SocialRevisions.CountAsync(Ct);
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

    /// <summary>A handler whose behaviour is set per test; the capability itself is AF.6.3's own suite.</summary>
    private sealed class ScriptedTaskHandler : IAiTaskHandler
    {
        public Func<AiTaskExecutionContext, CancellationToken, Task<AiTaskHandlerOutcome>> Behavior { get; set; } =
            (_, _) => Task.FromResult(AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation, "No behaviour was scripted for this test.", []));

        public Task<AiTaskHandlerOutcome> HandleAsync(
            AiTaskExecutionContext context, CancellationToken cancellationToken) =>
            Behavior(context, cancellationToken);
    }
}
