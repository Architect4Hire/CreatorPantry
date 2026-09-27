using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Polly.Timeout;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// Two <see cref="AiTaskType.RecipeConcepts"/> operations, two workspaces, one worker pass, against the real
/// <see cref="RecipeConceptsAiTaskHandler"/> and a fake <see cref="IChatClient"/> that answers according to
/// which brief it was actually sent. This is the one thing <see cref="AiOperationWorkerTests"/>' generic
/// two-workspace case cannot prove: not just that each operation runs under its own <c>WorkspaceId</c>, but
/// that the brief reaching each operation's own <c>AiTaskExecutionContext.Inputs</c> is its own and never the
/// other workspace's.
/// </summary>
public sealed class RecipeConceptsWorkerIsolationTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb");
    private static readonly Guid MembershipA = Guid.Parse("cccccccc-1111-1111-1111-cccccccccccc");
    private static readonly Guid MembershipB = Guid.Parse("dddddddd-2222-2222-2222-dddddddddddd");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public RecipeConceptsWorkerIsolationTests()
    {
        _connection.Open();

        var chatClient = new BriefEchoingChatClient();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddTenancy()
            .AddAudit()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton<IPromptTemplateStore>(EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly))
            .AddSingleton(BuildGateway(chatClient))
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .AddScoped<IAiOperationWorker, AiOperationWorker>()
            .AddKeyedScoped<IAiTaskHandler, RecipeConceptsAiTaskHandler>(AiTaskType.RecipeConcepts)
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

        db.Users.AddRange(
            new ApplicationUser
            {
                Id = "user-a", UserName = "user-a", NormalizedUserName = "USER-A",
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
                Id = MembershipA, WorkspaceId = WorkspaceA, UserId = "user-a",
                Role = WorkspaceRole.Contributor, Status = WorkspaceMembershipStatus.Active, JoinedAt = Now,
            },
            new WorkspaceMembership
            {
                Id = MembershipB, WorkspaceId = WorkspaceB, UserId = "user-b",
                Role = WorkspaceRole.Contributor, Status = WorkspaceMembershipStatus.Active, JoinedAt = Now,
            });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Each_operations_brief_reaches_only_its_own_execution_context()
    {
        var operationA = await QueueAsync(WorkspaceA, MembershipA, "key-a", "Sichuan");
        var operationB = await QueueAsync(WorkspaceB, MembershipB, "key-b", "Tuscan");

        using (var debugScope = _provider.CreateScope())
        {
            var db = debugScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var rows = await db.AiOperations.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                $"rows={rows.Count} statuses=[{string.Join(",", rows.Select(r => $"{r.Id}:{r.Status}:{r.AvailableAt:O}"))}]",
                "debug");
        }

        using var workerScope = _provider.CreateScope();
        var summary = await workerScope.ServiceProvider.GetRequiredService<IAiOperationWorker>()
            .RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.Claimed);
        Assert.Equal(2, summary.Proposed);

        var titleA = await FirstConceptTitleAsync(WorkspaceA, operationA);
        var titleB = await FirstConceptTitleAsync(WorkspaceB, operationB);

        Assert.Contains("Sichuan", titleA, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuscan", titleA, StringComparison.Ordinal);

        Assert.Contains("Tuscan", titleB, StringComparison.Ordinal);
        Assert.DoesNotContain("Sichuan", titleB, StringComparison.Ordinal);
    }

    private async Task<Guid> QueueAsync(Guid workspaceId, Guid membershipId, string key, string cuisine)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspaceId,
                TaskType = AiTaskType.RecipeConcepts,
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = key,
                TaskInputsJson = $$"""{"cuisine":"{{cuisine}}"}""",
                RequestedByMembershipId = membershipId,
                RequestedAt = Now,
                StatusChangedAt = Now,
                AvailableAt = Now,
            },
            TestContext.Current.CancellationToken);

        return requested.Operation!.Id;
    }

    private async Task<string> FirstConceptTitleAsync(Guid workspaceId, Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var proposal = await db.AiProposals.AsNoTracking()
            .Include(p => p.Changes)
            .SingleAsync(p => p.AiOperationId == operationId, TestContext.Current.CancellationToken);

        return proposal.Changes.Single(c => c.ChangeKind is AiChangeKind.Add).AfterValue!;
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            workspaceId == WorkspaceA ? MembershipA : MembershipB,
            WorkspaceRole.Owner);

    private static AiCompletionGateway BuildGateway(IChatClient client)
    {
        const string pipelineKey = "isolation-ai";

        var services = new ServiceCollection();
        services.AddResiliencePipeline(pipelineKey, pipeline => pipeline
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) }));
        var provider = services.BuildServiceProvider();

        return new AiCompletionGateway(
            client,
            new DefaultAiFailureClassifier(),
            new ConfiguredAiCostEstimator(new AiCostOptions()),
            new StoppedClock(),
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            new AiGatewayOptions
            {
                ResiliencePipelineKey = pipelineKey,
                ProviderName = "test-provider",
                ModelName = "test-model",
            },
            NullLogger<AiCompletionGateway>.Instance);
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    /// <summary>
    /// Answers with a concept titled after whichever brief keyword its own messages actually carried --
    /// proving, per call, that the handler rendered the operation's own brief and not another one's.
    /// </summary>
    private sealed class BriefEchoingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var text = string.Concat(messages.Select(message => message.Text));

            var keyword = text.Contains("Sichuan", StringComparison.Ordinal) ? "Sichuan"
                : text.Contains("Tuscan", StringComparison.Ordinal) ? "Tuscan"
                : throw new InvalidOperationException("Neither expected brief keyword was found in the prompt.");

            var payload = $$"""
                {
                  "schemaVersion": "recipe.concepts.v1",
                  "concepts": [
                    { "title": "{{keyword}} Special", "summary": "s", "distinctnessRationale": "r" },
                    { "title": "Other One", "summary": "s", "distinctnessRationale": "r" }
                  ]
                }
                """;

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, payload)) { ModelId = "test-model" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gateway does not stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
