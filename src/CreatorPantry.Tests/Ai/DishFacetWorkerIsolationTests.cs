using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
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
/// Two <see cref="AiTaskType.DishFacetSuggestion"/> operations, two workspaces, one worker pass, against the
/// real <see cref="DishFacetSuggestionAiTaskHandler"/> and a fake <see cref="IChatClient"/> that answers
/// according to which dish name it was actually sent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this proves that the request-seam tests cannot.</strong> Those run no worker, so every row
/// they poll is still <c>Requested</c> — the boundary they demonstrate is the one around an empty shell. This
/// one runs the operations through, so the assertions are about the proposal: that each was written under its
/// own <c>WorkspaceId</c>, and that the dish name reaching each operation's own prompt was its own.
/// </para>
/// <para>
/// <strong>A dish name is the creator's unpublished editorial plan.</strong> It is modest as secrets go, but
/// "what is the workspace next door working on" is precisely the question a crossed brief would answer, and
/// here the name is the <em>entire</em> input — so a handler that read the wrong operation's inputs would
/// produce a reading of a neighbour's dish with nothing in the row to show it had happened.
/// </para>
/// </remarks>
public sealed class DishFacetWorkerIsolationTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb");
    private static readonly Guid MembershipA = Guid.Parse("cccccccc-1111-1111-1111-cccccccccccc");
    private static readonly Guid MembershipB = Guid.Parse("dddddddd-2222-2222-2222-dddddddddddd");
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A's dish, which reads as a salad. B's reads as a main course, so the two cannot be confused.</summary>
    private const string FattoushA = "Fatoosh Salad with Radishes and Grilled Chicken Schwarma";

    private const string GaletteB = "Autumn Squash Galette";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public DishFacetWorkerIsolationTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddTenancy()
            .AddAudit()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton<IPromptTemplateStore>(EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly))
            .AddSingleton<IVocabularyFacade>(new StubVocabulary())
            .AddSingleton<IAiCompletionGateway>(BuildGateway(new NameEchoingChatClient()))
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddApplicationTime()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .AddScoped<IAiOperationWorker, AiOperationWorker>()
            .AddKeyedScoped<IAiTaskHandler, DishFacetSuggestionAiTaskHandler>(AiTaskType.DishFacetSuggestion)
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

    /// <summary>
    /// Each operation's dish name reaches only its own prompt, and its reading is stored under its own
    /// workspace.
    /// </summary>
    [Fact]
    public async Task Each_operations_dish_name_reaches_only_its_own_execution_context()
    {
        var operationA = await QueueAsync(WorkspaceA, MembershipA, "key-a", FattoushA);
        var operationB = await QueueAsync(WorkspaceB, MembershipB, "key-b", GaletteB);

        using var workerScope = _provider.CreateScope();
        var summary = await workerScope.ServiceProvider.GetRequiredService<IAiOperationWorker>()
            .RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.Claimed);
        Assert.Equal(2, summary.Proposed);

        var readingA = await ReadingOf(WorkspaceA, operationA);
        var readingB = await ReadingOf(WorkspaceB, operationB);

        // The Add row carries the name that was read, so it says which name the handler actually had.
        Assert.Equal(FattoushA, readingA.NameRead);
        Assert.Equal(GaletteB, readingB.NameRead);

        // The fake answers from whichever name it was sent, so the stored course proves the prompt carried
        // that workspace's name and not the other's.
        Assert.Equal("salad", readingA.DishType);
        Assert.Equal("main-course", readingB.DishType);

        // Every row of each proposal belongs to the workspace that asked.
        Assert.All(readingA.WorkspaceIds, id => Assert.Equal(WorkspaceA, id));
        Assert.All(readingB.WorkspaceIds, id => Assert.Equal(WorkspaceB, id));
    }

    /// <summary>
    /// Once a reading exists, the neighbour still cannot read it — and it reads as absent, not refused.
    /// </summary>
    /// <remarks>
    /// The half the request-seam test cannot reach: it polls rows that are still <c>Requested</c>, where there
    /// is no proposal to leak. This asserts the boundary over a row that has a creator's dish name and a
    /// model's reading of it actually stored against it.
    /// </remarks>
    [Fact]
    public async Task A_completed_reading_cannot_be_read_from_the_other_workspace()
    {
        var operationA = await QueueAsync(WorkspaceA, MembershipA, "key-a", FattoushA);

        using var workerScope = _provider.CreateScope();
        await workerScope.ServiceProvider.GetRequiredService<IAiOperationWorker>()
            .RunPendingAsync(TestContext.Current.CancellationToken);

        // Present under its own workspace, with the proposal attached.
        using var ownScope = _provider.CreateScope();
        Resolve(ownScope, WorkspaceA);
        var own = await ownScope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>()
            .GetWithProposalAsync(operationA, TestContext.Current.CancellationToken);

        Assert.NotNull(own);
        Assert.NotNull(own.Proposal);

        // Absent from the neighbour's, which is what the 404 on the route is built on.
        using var neighbourScope = _provider.CreateScope();
        Resolve(neighbourScope, WorkspaceB);
        var crossing = await neighbourScope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>()
            .GetWithProposalAsync(operationA, TestContext.Current.CancellationToken);

        Assert.Null(crossing);
    }

    private async Task<Guid> QueueAsync(Guid workspaceId, Guid membershipId, string key, string dishName)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspaceId,
                TaskType = AiTaskType.DishFacetSuggestion,
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = key,
                TaskInputsJson = $$"""{"dishName":"{{dishName}}"}""",
                RequestedByMembershipId = membershipId,
                RequestedAt = Now,
                StatusChangedAt = Now,
                AvailableAt = Now,
            },
            TestContext.Current.CancellationToken);

        return requested.Operation!.Id;
    }

    private async Task<StoredReading> ReadingOf(Guid workspaceId, Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var proposal = await db.AiProposals.AsNoTracking()
            .Include(p => p.Changes)
            .SingleAsync(p => p.AiOperationId == operationId, TestContext.Current.CancellationToken);

        return new StoredReading(
            proposal.Changes.Single(c => c.ChangeKind is AiChangeKind.Add).AfterValue!,
            proposal.Changes
                .SingleOrDefault(c => c.FieldName == AiDishFacetFields.Code(AiDishFacet.DishType))
                ?.AfterValue,
            proposal.Changes.Select(c => c.WorkspaceId).ToList());
    }

    private sealed record StoredReading(
        string NameRead, string? DishType, IReadOnlyList<Guid> WorkspaceIds);

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            workspaceId == WorkspaceA ? MembershipA : MembershipB,
            WorkspaceRole.Owner, "test-account");

    private static AiCompletionGateway BuildGateway(IChatClient client)
    {
        const string pipelineKey = "isolation-dish-facets";

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
    /// Answers with the course implied by whichever dish name its own messages actually carried — proving,
    /// per call, that the handler rendered the operation's own name and not another one's.
    /// </summary>
    private sealed class NameEchoingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var text = string.Concat(messages.Select(message => message.Text));

            var course = text.Contains(FattoushA, StringComparison.Ordinal) ? "salad"
                : text.Contains(GaletteB, StringComparison.Ordinal) ? "main-course"
                : throw new InvalidOperationException("Neither expected dish name was found in the prompt.");

            var payload = $$"""
                {
                  "schemaVersion": "recipe.dish-facets.v1",
                  "suggestions": [
                    {
                      "facet": "DishType",
                      "code": "{{course}}",
                      "confidence": "Likely",
                      "rationale": "read from the name"
                    }
                  ]
                }
                """;

            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, payload)) { ModelId = "test-model" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gateway does not stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The global vocabulary, which has no workspace dimension to isolate: the same rows for both workspaces,
    /// by design (tenancy.md). Both courses the fake can answer with are present.
    /// </summary>
    private sealed class StubVocabulary : IVocabularyFacade
    {
        public Task<IReadOnlyList<ReferenceEntryServiceModel>> ListActiveCuisinesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReferenceEntryServiceModel>>(
                [new ReferenceEntryServiceModel(Guid.NewGuid(), "levantine", "Levantine")]);

        public Task<IReadOnlyList<ReferenceEntryServiceModel>> ListActiveCoursesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReferenceEntryServiceModel>>(
                [
                    new ReferenceEntryServiceModel(Guid.NewGuid(), "salad", "Salad"),
                    new ReferenceEntryServiceModel(Guid.NewGuid(), "main-course", "Main course"),
                ]);

        public Task<IReadOnlyList<CookingTechniqueServiceModel>> ListActiveTechniquesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CookingTechniqueServiceModel>>(
                [new CookingTechniqueServiceModel(Guid.NewGuid(), "grill", "Grill", false)]);

        public Task<bool> IsUsableAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
            throw Unused();

        public Task<string?> GetDisplayNameAsync(
            VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) => throw Unused();

        public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListFoodCategoriesAsync(
            ReferenceQueryViewModel query, CancellationToken cancellationToken) => throw Unused();

        public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListCuisinesAsync(
            ReferenceQueryViewModel query, CancellationToken cancellationToken) => throw Unused();

        public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListCoursesAsync(
            ReferenceQueryViewModel query, CancellationToken cancellationToken) => throw Unused();

        public Task<OperationResult<CursorPageServiceModel<CookingTechniqueServiceModel>>> ListTechniquesAsync(
            ReferenceQueryViewModel query, CancellationToken cancellationToken) => throw Unused();

        public Task<OperationResult<CursorPageServiceModel<ReferenceEntryServiceModel>>> ListEquipmentTypesAsync(
            ReferenceQueryViewModel query, CancellationToken cancellationToken) => throw Unused();

        public Task<OperationResult<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>>
            ListDietaryProfilesAsync(ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
            throw Unused();

        public Task<OperationResult<CursorPageServiceModel<DescribedReferenceEntryServiceModel>>>
            ListAllergensAsync(ReferenceQueryViewModel query, CancellationToken cancellationToken) =>
            throw Unused();

        private static NotSupportedException Unused() =>
            new("This handler reads the active catalogues, never a page of one.");
    }
}
