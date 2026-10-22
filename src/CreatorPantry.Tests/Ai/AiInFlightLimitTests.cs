using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The AI in-progress caps (15.3): queued or running operations per member and per workspace, counted from the
/// operations themselves at the one gate every AI request passes through.
/// </summary>
public sealed class AiInFlightLimitTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MemberOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MemberTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly LimitsOptions _limits = new() { AiInFlightPerMember = 2, AiInFlightPerWorkspace = 3 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AiInFlightLimitTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.RecipeConcepts);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(tasks)
            .AddAiTaskGate()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddScoped<CreatorPantry.Domain.Modules.Content.Facade.ICreativeContextFacade, NoCreativeContexts>()
            .AddScoped<CreatorPantry.Domain.Modules.Content.Facade.ICreativeContextPackageFacade, NoCreativeContexts>()
            .AddApplicationTime()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiRequestQuotaGate, AiRequestQuotaGate>()
            .AddScoped<IAiConceptRequestBusiness, AiConceptRequestBusiness>()
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(_limits))
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_member_with_their_share_queued_is_refused_and_nothing_is_written()
    {
        await RequestAsync(WorkspaceA, MemberOne, "one");
        await RequestAsync(WorkspaceA, MemberOne, "two");

        var refused = await RequestRawAsync(WorkspaceA, MemberOne, "three");

        Assert.Equal(LimitErrorCodes.ConcurrencyExhausted, refused.Code);
        Assert.Equal("User", refused.Extensions!["scope"]);
        Assert.Equal("AiRequest", refused.Extensions["operation"]);
        Assert.Equal(2, refused.Extensions["limit"]);

        // Nothing to wait for but work finishing, so no instant is promised.
        Assert.False(refused.Extensions.ContainsKey("resetsAt"));
        Assert.Equal(2, await CountAsync(WorkspaceA));
    }

    [Fact]
    public async Task A_retry_at_the_cap_is_answered_with_the_operation_it_already_bought()
    {
        var first = await RequestAsync(WorkspaceA, MemberOne, "one");
        await RequestAsync(WorkspaceA, MemberOne, "two");

        // The member is at their cap, and their first request's response was lost: the retry is that request.
        var retried = await RequestAsync(WorkspaceA, MemberOne, "one");

        Assert.Equal(first, retried);
        Assert.Equal(2, await CountAsync(WorkspaceA));

        // A new key is still a new request, and still refused.
        Assert.Equal(LimitErrorCodes.ConcurrencyExhausted, (await RequestRawAsync(WorkspaceA, MemberOne, "three")).Code);
    }

    [Fact]
    public async Task The_workspace_cap_counts_every_member()
    {
        await RequestAsync(WorkspaceA, MemberOne, "one");
        await RequestAsync(WorkspaceA, MemberOne, "two");
        await RequestAsync(WorkspaceA, MemberTwo, "three");

        // Member two is inside their own share, and the workspace is not.
        var refused = await RequestRawAsync(WorkspaceA, MemberTwo, "four");

        Assert.Equal(LimitErrorCodes.ConcurrencyExhausted, refused.Code);
        Assert.Equal("Workspace", refused.Extensions!["scope"]);
        Assert.Equal(3, refused.Extensions["limit"]);
    }

    [Fact]
    public async Task Work_that_finishes_frees_its_place()
    {
        await RequestAsync(WorkspaceA, MemberOne, "one");
        var second = await RequestAsync(WorkspaceA, MemberOne, "two");

        Assert.Equal(LimitErrorCodes.ConcurrencyExhausted, (await RequestRawAsync(WorkspaceA, MemberOne, "three")).Code);

        await SettleAsync(WorkspaceA, second, AiOperationStatus.Proposed);

        await RequestAsync(WorkspaceA, MemberOne, "three");
    }

    [Fact]
    public async Task One_workspace_at_its_cap_leaves_the_other_untouched_and_says_nothing_about_it()
    {
        await RequestAsync(WorkspaceA, MemberOne, "one");
        await RequestAsync(WorkspaceA, MemberOne, "two");
        await RequestAsync(WorkspaceA, MemberTwo, "three");

        // The same member ids in B, which share nothing with A: B's queue is empty, so B is admitted.
        await RequestAsync(WorkspaceB, MemberOne, "one");
        await RequestAsync(WorkspaceB, MemberTwo, "two");

        var refused = await RequestRawAsync(WorkspaceA, MemberTwo, "four");

        Assert.Equal(LimitErrorCodes.ConcurrencyExhausted, refused.Code);

        // Only A's own allowance; no workspace, member, account or count of anyone else's.
        Assert.Equal(["limit", "operation", "scope"], refused.Extensions!.Keys.Order());
        Assert.DoesNotContain(WorkspaceB.ToString(), refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await CountAsync(WorkspaceB));
    }

    [Fact]
    public async Task A_batch_counts_as_every_operation_it_would_queue()
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, WorkspaceA, MemberOne);
        var gate = scope.ServiceProvider.GetRequiredService<IAiRequestQuotaGate>();

        var refused = await gate.RefuseIfRunsUnaffordableAsync(
            AiTaskType.LongFormSection, runs: 3, "sectionsRequested", "sectionsCoverable", Ct);

        Assert.Equal(LimitErrorCodes.ConcurrencyExhausted, refused?.Code);

        Assert.Null(await gate.RefuseIfRunsUnaffordableAsync(
            AiTaskType.LongFormSection, runs: 2, "sectionsRequested", "sectionsCoverable", Ct));
    }

    [Fact]
    public async Task Switched_off_there_is_no_cap()
    {
        _limits.Enabled = false;

        for (var key = 0; key < 5; key++)
        {
            await RequestAsync(WorkspaceA, MemberOne, $"key-{key}");
        }

        Assert.Equal(5, await CountAsync(WorkspaceA));
    }

    private async Task<Guid> RequestAsync(Guid workspaceId, Guid membershipId, string key)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId, membershipId);

        var outcome = await scope.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>()
            .RequestAsync(new RequestRecipeConceptsViewModel { Cuisine = "Sichuan" }, key, Ct);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Code);

        return outcome.Result.Value!.AiProposalRequestId;
    }

    private async Task<Domain.Managers.Results.OperationError> RequestRawAsync(Guid workspaceId, Guid membershipId, string key)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId, membershipId);

        var outcome = await scope.ServiceProvider.GetRequiredService<IAiConceptRequestBusiness>()
            .RequestAsync(new RequestRecipeConceptsViewModel { Cuisine = "Sichuan" }, key, Ct);

        Assert.False(outcome.Result.Succeeded);

        return outcome.Result.Error!;
    }

    private async Task SettleAsync(Guid workspaceId, Guid operationId, AiOperationStatus status)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId, MemberOne);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        (await db.AiOperations.SingleAsync(operation => operation.Id == operationId, Ct)).Status = status;
        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> CountAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, workspaceId, MemberOne);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AiOperations.CountAsync(Ct);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId, Guid membershipId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            membershipId,
            WorkspaceRole.Owner,
            "account-" + membershipId.ToString("N")[..4]);

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
