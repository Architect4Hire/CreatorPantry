using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
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

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>
/// Runs a fixture against the full SQLite-backed <see cref="IAiOperationWorker"/> pipeline — the heaviest
/// kind, and the only one that needs a real database and, for the safety-block scenario, a real
/// <see cref="AiCompletionGateway"/> wrapping a fake <see cref="IChatClient"/>. Covers WorkspaceIsolation and
/// the provider-classification half of RefusalSafety. Each scenario builds and tears down its own isolated
/// environment, mirroring the fixture shape <c>AiOperationWorkerTests</c> already established.
/// </summary>
internal sealed class WorkerOperationCase : IAiEvaluationCase
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid MembershipB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    public AiEvaluationKind Kind => AiEvaluationKind.WorkerOperation;

    public async Task<AiEvaluationVerdict> RunAsync(AiEvaluationFixture fixture, CancellationToken cancellationToken)
    {
        var input = fixture.Input.Deserialize<Input>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new AiEvaluationException(fixture.Identity, "'input' is null.");
        var expect = fixture.Expect.Deserialize<Expect>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new AiEvaluationException(fixture.Identity, "'expect' is null.");

        return input.Scenario switch
        {
            "workspace-isolation" => await WorkspaceIsolationAsync(expect, cancellationToken),
            "provider-safety-block" => await ProviderSafetyBlockAsync(expect, cancellationToken),
            _ => throw new AiEvaluationException(fixture.Identity, $"unknown scenario '{input.Scenario}'."),
        };
    }

    /// <summary>
    /// Two operations, two workspaces, one pass: each must be resolved and run against its own workspace only,
    /// and its resulting proposal must never carry the other's workspace id.
    /// </summary>
    private static async Task<AiEvaluationVerdict> WorkspaceIsolationAsync(Expect expect, CancellationToken cancellationToken)
    {
        var clock = new MovableClock(DateTimeOffset.UnixEpoch);
        var handler = new ScriptedTaskHandler((context, _) => Task.FromResult(Success(context.OperationId, context.WorkspaceId, clock.UtcNow)));

        await using var environment = Build(clock, handler);
        environment.Seed();

        var operationA = await environment.RequestAsync(WorkspaceA, MembershipA, "key-a");
        var operationB = await environment.RequestAsync(WorkspaceB, MembershipB, "key-b");

        var summary = await environment.RunPendingAsync(cancellationToken);

        if (summary.Claimed != 2 || summary.Proposed != 2)
        {
            return AiEvaluationVerdict.Fail(
                $"expected both operations claimed and proposed, got claimed={summary.Claimed} proposed={summary.Proposed}.");
        }

        var proposalA = await environment.SingleProposalAsync(WorkspaceA);
        var proposalB = await environment.SingleProposalAsync(WorkspaceB);

        if (proposalA.WorkspaceId != WorkspaceA || proposalA.AiOperationId != operationA)
        {
            return AiEvaluationVerdict.Fail("workspace A's proposal does not belong to workspace A's own operation.");
        }

        if (proposalB.WorkspaceId != WorkspaceB || proposalB.AiOperationId != operationB)
        {
            return AiEvaluationVerdict.Fail("workspace B's proposal does not belong to workspace B's own operation.");
        }

        return expect.Outcome == "Proposed"
            ? AiEvaluationVerdict.Pass()
            : AiEvaluationVerdict.Fail($"expected outcome '{expect.Outcome}', but both operations proposed cleanly.");
    }

    /// <summary>
    /// A handler that calls the real gateway against a fake <see cref="IChatClient"/> throwing a
    /// provider-shaped safety refusal, classified by a provider-aware classifier the way
    /// <c>CreatorPantry.AiProvider</c>'s real one would. One attempt, never retried.
    /// </summary>
    private static async Task<AiEvaluationVerdict> ProviderSafetyBlockAsync(Expect expect, CancellationToken cancellationToken)
    {
        var clock = new MovableClock(DateTimeOffset.UnixEpoch);
        var chatClient = new AlwaysThrowingChatClient(() => new SafetyRefusedException());
        var handler = new ScriptedTaskHandler(async (context, ct) =>
        {
            var gateway = BuildGateway(chatClient, clock);
            var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
                .WithTask("Propose nothing; this is a fixture.")
                .WithOutputSchema(AiOutputSchema.Json)
                .Build();

            var outcome = await gateway.CompleteAsync(
                new AiCompletionRequest(envelope, "fixture.worker.v1", context.Scope, "fixture.worker", "1.0.0", context.CorrelationId),
                ct);

            return outcome.Succeeded
                ? throw new InvalidOperationException("expected the fake provider to be refused.")
                : AiTaskHandlerOutcome.ForFailure(outcome.Failure!.Category, outcome.Failure.Message, outcome.Attempts);
        });

        await using var environment = Build(clock, handler);
        environment.Seed();

        await environment.RequestAsync(WorkspaceA, MembershipA, "key-a");
        var summary = await environment.RunPendingAsync(cancellationToken);

        if (summary.Failed != 1)
        {
            return AiEvaluationVerdict.Fail($"expected one failed operation, got failed={summary.Failed}.");
        }

        var operation = await environment.SingleOperationAsync(WorkspaceA);

        if (operation.FailureCategory?.ToString() != expect.FailureCategory)
        {
            return AiEvaluationVerdict.Fail(
                $"expected failure category '{expect.FailureCategory}', got '{operation.FailureCategory}'.");
        }

        var attempts = await environment.CountExecutionRowsAsync(WorkspaceA);

        if (expect.AttemptCount is { } expectedAttempts && attempts != expectedAttempts)
        {
            return AiEvaluationVerdict.Fail($"expected {expectedAttempts} recorded attempt(s), got {attempts}.");
        }

        return AiEvaluationVerdict.Pass();
    }

    private static AiTaskHandlerOutcome Success(Guid operationId, Guid workspaceId, DateTimeOffset now) =>
        AiTaskHandlerOutcome.ForProposal(
            new AiProposal
            {
                WorkspaceId = workspaceId,
                AiOperationId = operationId,
                OutputSchemaVersion = "fixture.v1",
                PromptTemplateId = "fixture.worker",
                PromptTemplateVersion = "1.0.0",
                PromptTemplateBodyChecksum = "sha256:fixture",
                ProviderName = "fixture-provider",
                ModelName = "fixture-model",
                CreatedAt = now,
            },
            []);

    private static AiCompletionGateway BuildGateway(IChatClient client, IClock clock)
    {
        const string pipelineKey = "eval-ai";

        var services = new ServiceCollection();

        // Mirrors AddAiResilience's own ShouldHandle predicate exactly (Worker/Program.cs): only an
        // AiTransientFailureException is retried. Present so the safety-block scenario's "never retried"
        // claim is a real proof -- a SafetyRefusedException does not match this predicate and is not
        // retried, rather than the claim holding only because no retry strategy existed to try.
        services.AddResiliencePipeline(pipelineKey, pipeline => pipeline
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is AiTransientFailureException),
            })
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) }));
        var provider = services.BuildServiceProvider();

        return new AiCompletionGateway(
            client,
            new SafetyAwareClassifier(),
            new ConfiguredAiCostEstimator(new AiCostOptions()),
            clock,
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            new AiGatewayOptions
            {
                ResiliencePipelineKey = pipelineKey,
                ProviderName = "fixture-provider",
                ModelName = "fixture-model",
                ModelDeployment = "fixture-deployment",
            },
            NullLogger<AiCompletionGateway>.Instance);
    }

    private static Environment Build(IClock clock, IAiTaskHandler handler)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var provider = new ServiceCollection()
            .AddLogging()
            .AddTenancy()
            .AddSingleton(clock)
            .AddAudit()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .AddScoped<IAiOperationWorker, AiOperationWorker>()
            .AddKeyedSingleton<IAiTaskHandler>(AiTaskType.Diagnostic, handler)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        return new Environment(connection, provider, clock);
    }

    private sealed class Environment(SqliteConnection connection, ServiceProvider provider, IClock clock) : IAsyncDisposable
    {
        public void Seed()
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.Database.EnsureCreated();

            db.Workspaces.AddRange(
                new Workspace { Id = WorkspaceA, Name = "A", Slug = "a", CreatedAt = clock.UtcNow },
                new Workspace { Id = WorkspaceB, Name = "B", Slug = "b", CreatedAt = clock.UtcNow });

            db.Users.AddRange(
                new ApplicationUser
                {
                    Id = "user-a", UserName = "user-a", NormalizedUserName = "USER-A",
                    Email = "user-a@example.com", NormalizedEmail = "USER-A@EXAMPLE.COM",
                    DisplayName = "User A", CreatedAt = clock.UtcNow,
                },
                new ApplicationUser
                {
                    Id = "user-b", UserName = "user-b", NormalizedUserName = "USER-B",
                    Email = "user-b@example.com", NormalizedEmail = "USER-B@EXAMPLE.COM",
                    DisplayName = "User B", CreatedAt = clock.UtcNow,
                });

            db.WorkspaceMemberships.AddRange(
                new WorkspaceMembership
                {
                    Id = MembershipA, WorkspaceId = WorkspaceA, UserId = "user-a",
                    Role = WorkspaceRole.Contributor, Status = WorkspaceMembershipStatus.Active, JoinedAt = clock.UtcNow,
                },
                new WorkspaceMembership
                {
                    Id = MembershipB, WorkspaceId = WorkspaceB, UserId = "user-b",
                    Role = WorkspaceRole.Contributor, Status = WorkspaceMembershipStatus.Active, JoinedAt = clock.UtcNow,
                });

            db.SaveChanges();
        }

        public async Task<Guid> RequestAsync(Guid workspaceId, Guid membershipId, string key)
        {
            using var scope = provider.CreateScope();
            Resolve(scope, workspaceId);
            var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();

            var requested = await operations.RequestAsync(
                new AiOperation
                {
                    WorkspaceId = workspaceId,
                    TaskType = AiTaskType.Diagnostic,
                    Scope = AiOperationScope.WholeRecipe,
                    Status = AiOperationStatus.Requested,
                    IdempotencyKey = key,
                    RequestedByMembershipId = membershipId,
                    RequestedAt = clock.UtcNow,
                    StatusChangedAt = clock.UtcNow,
                    AvailableAt = clock.UtcNow,
                },
                CancellationToken.None);

            return requested.Operation!.Id;
        }

        public async Task<AiOperationWorkerPassSummary> RunPendingAsync(CancellationToken cancellationToken)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>().RunPendingAsync(cancellationToken);
        }

        public async Task<AiProposal> SingleProposalAsync(Guid workspaceId)
        {
            using var scope = provider.CreateScope();
            Resolve(scope, workspaceId);
            return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .AiProposals.AsNoTracking().SingleAsync();
        }

        public async Task<AiOperation> SingleOperationAsync(Guid workspaceId)
        {
            using var scope = provider.CreateScope();
            Resolve(scope, workspaceId);
            return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .AiOperations.AsNoTracking().SingleAsync();
        }

        public async Task<int> CountExecutionRowsAsync(Guid workspaceId)
        {
            using var scope = provider.CreateScope();
            Resolve(scope, workspaceId);
            return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .AiExecutionMetadata.CountAsync();
        }

        private static void Resolve(IServiceScope scope, Guid workspaceId) =>
            scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
                workspaceId,
                workspaceId == WorkspaceA ? "a" : "b",
                workspaceId == WorkspaceA ? MembershipA : MembershipB,
                WorkspaceRole.Owner);

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ScriptedTaskHandler(
        Func<AiTaskExecutionContext, CancellationToken, Task<AiTaskHandlerOutcome>> behavior) : IAiTaskHandler
    {
        public Task<AiTaskHandlerOutcome> HandleAsync(AiTaskExecutionContext context, CancellationToken cancellationToken) =>
            behavior(context, cancellationToken);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; } = start;
    }

    /// <summary>A scripted <see cref="IChatClient"/> that always throws, standing in for a provider refusal.</summary>
    private sealed class AlwaysThrowingChatClient(Func<Exception> exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw exception();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gateway does not stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Stands in for a provider SDK's content-filter exception.</summary>
    private sealed class SafetyRefusedException() : Exception("content filtered");

    /// <summary>
    /// What a provider-specific classifier does: recognise the SDK's own refusal type. The domain default
    /// cannot, which is exactly why a real one lives in <c>CreatorPantry.AiProvider</c> — see ai.md.
    /// </summary>
    private sealed class SafetyAwareClassifier : DefaultAiFailureClassifier
    {
        public override AiFailureClassification Classify(Exception exception, CancellationToken callerToken) =>
            exception is SafetyRefusedException
                ? new AiFailureClassification(AiFailureCategory.SafetyBlocked, false, null, "The provider's safety filter refused it.")
                : base.Classify(exception, callerToken);
    }

    private sealed record Input(string Scenario);

    private sealed record Expect(string Outcome, string? FailureCategory, int? AttemptCount);
}
