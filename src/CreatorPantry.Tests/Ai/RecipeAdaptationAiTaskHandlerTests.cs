using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
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
/// AIREC-005's task handler against a fake <see cref="IChatClient"/> and the real recipe module — no network,
/// no model. Covers goal routing, the fixed <see cref="AiOperationScope.WholeRecipe"/> scope, what reaches the
/// prompt and where, the deterministic-yield wiring, and the failure paths.
/// </summary>
/// <remarks>
/// The recipe module is real rather than stubbed, for the reason <c>IngredientSubstitutionAiTaskHandlerTests</c>
/// gives: the containment claims are about a real serialized snapshot. The seeded recipe's ingredient line
/// carries no structured quantity, so a yield goal against it has nothing to scale — which is not a gap in the
/// fixture, it is itself the case that proves the deterministic-figures-only rule and the honest-limitation
/// rule at once.
/// </remarks>
public sealed class RecipeAdaptationAiTaskHandlerTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operation = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    /// <summary>The creator's own wording, distinctive enough that its presence anywhere is unambiguous.</summary>
    private const string SelectedLine = "400ml buttermilk, shaken";

    private static readonly PromptTemplate Template =
        EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly).Get(AiTaskCatalog.RecipeAdaptation);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public RecipeAdaptationAiTaskHandlerTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddIdempotency(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
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

    // ---- what a valid answer becomes ---------------------------------------------------------------------

    [Fact]
    public async Task A_valid_dietary_answer_becomes_a_whole_recipe_proposal_pinned_to_the_version()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(FakeChatClient.Returning(DietaryChange), recipe, AiAdaptationGoal.Dietary, "gluten-free");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        Assert.Equal(Template.OutputSchemaVersion, proposal.OutputSchemaVersion);
        Assert.Equal(Template.Id, proposal.PromptTemplateId);
        Assert.Equal(recipe.VersionId, proposal.SourceRecipeVersionId);

        var change = Assert.Single(proposal.Changes);
        Assert.Equal(AiChangeTargetKind.Recipe, change.TargetKind);
        Assert.Equal("title", change.FieldName);
    }

    /// <summary>
    /// A goal that cannot be met produces a limitation, not fabricated success — and this is still a
    /// successful operation, because the honest answer is what was asked for.
    /// </summary>
    [Fact]
    public async Task A_goal_that_cannot_be_met_succeeds_as_a_proposal_of_a_limitation_alone()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(FakeChatClient.Returning(LimitationOnly), recipe, AiAdaptationGoal.Dietary, "nut-free");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Empty(outcome.Proposal!.Changes);

        var warning = Assert.Single(outcome.Proposal.Warnings);
        Assert.Equal(AiWarningKind.Limitation, warning.Kind);
        Assert.Null(warning.AiStructuredChangeId);
    }

    /// <summary>Proposing nothing and explaining nothing is the fabricated-success failure mode this task exists to refuse.</summary>
    [Fact]
    public async Task An_unexplained_empty_answer_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(FakeChatClient.Returning(UnexplainedEmpty), recipe, AiAdaptationGoal.Dietary, "gluten-free");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Null(outcome.Proposal);
    }

    // ---- what reaches the prompt, and where --------------------------------------------------------------

    [Fact]
    public async Task The_creators_goal_detail_never_reaches_the_system_message()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(DietaryChange);

        await Run(client, recipe, AiAdaptationGoal.Dietary, "my reader cannot eat gluten");

        Assert.Contains("my reader cannot eat gluten", UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain("my reader cannot eat gluten", SystemMessage(client), StringComparison.Ordinal);
    }

    /// <summary>The per-goal instructions are fixed server text, safe to render into the task's own instructions.</summary>
    [Fact]
    public async Task The_declared_goals_fixed_instructions_reach_the_system_message()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(DietaryChange);

        await Run(client, recipe, AiAdaptationGoal.Dietary, "gluten-free");

        Assert.Contains("allergen and dietary consequence", SystemMessage(client), StringComparison.Ordinal);
    }

    /// <summary>A different declared goal renders different fixed instructions — one goal per operation.</summary>
    [Fact]
    public async Task A_different_goal_renders_different_instructions()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(DietaryChange);

        await Run(client, recipe, AiAdaptationGoal.Equipment, "no stand mixer");

        var system = SystemMessage(client);
        Assert.Contains("equipment named", system, StringComparison.Ordinal);
        Assert.DoesNotContain("allergen and dietary consequence", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absent_goal_detail_says_so_rather_than_leaving_a_blank()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(DietaryChange);

        await Run(client, recipe, AiAdaptationGoal.Dietary, detail: null);

        Assert.Contains("not given", UserMessage(client), StringComparison.Ordinal);
    }

    // ---- deterministic yield wiring -----------------------------------------------------------------------

    /// <summary>
    /// A yield goal computes before it asks: the deterministic figures reach the prompt as a reference, not as
    /// something the model was merely told about.
    /// </summary>
    [Fact]
    public async Task A_yield_goal_carries_the_deterministic_scaling_as_a_reference()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(YieldLimitation);

        await Run(client, recipe, AiAdaptationGoal.Yield, multiplier: 2m);

        Assert.Contains("\"Factor\"", UserMessage(client), StringComparison.Ordinal);
    }

    /// <summary>A non-yield goal never computes a scaling preview, so no reference segment is added.</summary>
    [Fact]
    public async Task A_non_yield_goal_carries_no_scaling_reference()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(DietaryChange);

        await Run(client, recipe, AiAdaptationGoal.Dietary, "gluten-free");

        Assert.DoesNotContain("\"Factor\"", UserMessage(client), StringComparison.Ordinal);
    }

    /// <summary>
    /// The seeded line carries no structured quantity, so there is nothing deterministic to scale it to — the
    /// honest answer is a limitation, and the handler accepts it exactly as it would for any other goal.
    /// </summary>
    [Fact]
    public async Task A_yield_goal_against_nothing_scalable_succeeds_as_a_limitation()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(FakeChatClient.Returning(YieldLimitation), recipe, AiAdaptationGoal.Yield, multiplier: 2m);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Empty(outcome.Proposal!.Changes);
    }

    /// <summary>The model may not invent an ingredient quantity even when nothing deterministic backs one.</summary>
    [Fact]
    public async Task A_yield_answer_inventing_a_quantity_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(
            FakeChatClient.Returning(YieldInventedQuantity(recipe.IngredientId)),
            recipe,
            AiAdaptationGoal.Yield,
            multiplier: 2m);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    [Fact]
    public async Task A_yield_goal_naming_no_target_fails_without_calling_the_model()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(YieldLimitation);

        var outcome = await Run(client, recipe, AiAdaptationGoal.Yield, multiplier: null, targetYieldQuantity: null);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    // ---- failure paths -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_operation_naming_no_goal_fails_without_calling_the_model()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(DietaryChange);

        var outcome = await Run(
            client, recipe, goal: null, inputs: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task An_operation_naming_no_recipe_is_refused()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(DietaryChange);

        var outcome = await Run(client, recipe, AiAdaptationGoal.Dietary, "gluten-free", recipeless: true);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// A worker running under workspace B, against an operation naming workspace A's recipe. The recipe is
    /// invisible through the facade, so the run fails before any provider call and none of A's content reaches
    /// a prompt (tenancy.md).
    /// </summary>
    [Fact]
    public async Task A_recipe_in_another_workspace_is_invisible_to_the_handler()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(DietaryChange);

        var outcome = await Run(client, recipe, AiAdaptationGoal.Dietary, "gluten-free", workspaceId: WorkspaceB);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    // ---- answers -----------------------------------------------------------------------------------------

    private const string DietaryChange = """
        {"schemaVersion":"recipe.adaptation.v1",
         "changes":[{"changeKind":"Set","targetKind":"Recipe","fieldName":"title","afterValue":"Gluten-Free Buttermilk Soda Bread"}],
         "warnings":[{"kind":"Rationale","message":"Retitled to flag the adaptation for a reader scanning the list.","changeIndex":0}]}
        """;

    private const string LimitationOnly = """
        {"schemaVersion":"recipe.adaptation.v1","changes":[],
         "warnings":[{"kind":"Limitation","message":"This recipe cannot be made nut-free without losing the intended texture."}]}
        """;

    private const string UnexplainedEmpty = """
        {"schemaVersion":"recipe.adaptation.v1","changes":[],"warnings":[]}
        """;

    private const string YieldLimitation = """
        {"schemaVersion":"recipe.adaptation.v1","changes":[],
         "warnings":[{"kind":"Limitation","message":"No ingredient line here carries a structured quantity to scale."}]}
        """;

    private static string YieldInventedQuantity(Guid ingredientId) => $$"""
        {"schemaVersion":"recipe.adaptation.v1",
         "changes":[{"changeKind":"Set","targetKind":"Ingredient","targetId":"{{ingredientId}}","fieldName":"quantity","afterValue":"800"}]}
        """;

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId, Guid IngredientId);

    private static string UserMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.User).Text;

    private static string SystemMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.System).Text;

    private async Task<AiTaskHandlerOutcome> Run(
        FakeChatClient client,
        SeededRecipe recipe,
        AiAdaptationGoal? goal = AiAdaptationGoal.Dietary,
        string? detail = "gluten-free",
        decimal? multiplier = null,
        decimal? targetYieldQuantity = null,
        IReadOnlyDictionary<string, string>? inputs = null,
        bool recipeless = false,
        Guid? workspaceId = null)
    {
        const string pipelineKey = "test-ai-adaptation";

        var services = new ServiceCollection();
        services.AddResiliencePipeline(pipelineKey, pipeline => pipeline
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is AiTransientFailureException),
            })
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) }));
        using var pipelines = services.BuildServiceProvider();

        var gateway = new AiCompletionGateway(
            client,
            new DefaultAiFailureClassifier(),
            new ConfiguredAiCostEstimator(new AiCostOptions()),
            new StoppedClock(),
            pipelines.GetRequiredService<ResiliencePipelineProvider<string>>(),
            new AiGatewayOptions
            {
                ResiliencePipelineKey = pipelineKey,
                ProviderName = "test-provider",
                ModelName = "test-model",
            },
            NullLogger<AiCompletionGateway>.Instance);

        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId ?? WorkspaceA);

        var handler = new RecipeAdaptationAiTaskHandler(
            gateway,
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly),
            new StoppedClock());

        inputs ??= Inputs(goal, detail, multiplier, targetYieldQuantity);

        var context = new AiTaskExecutionContext(
            Operation,
            workspaceId ?? WorkspaceA,
            LeaseToken: Guid.NewGuid(),
            AiOperationScope.WholeRecipe,
            RecipeId: recipeless ? null : recipe.RecipeId,
            RecipeVersionId: recipeless ? null : recipe.VersionId,
            CorrelationId: Guid.NewGuid(),
            RenewLeaseAsync: _ => Task.CompletedTask,
            Inputs: inputs);

        return await handler.HandleAsync(context, TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Inputs(
        AiAdaptationGoal? goal, string? detail, decimal? multiplier, decimal? targetYieldQuantity)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);

        if (goal is { } declared)
        {
            inputs[AiAdaptationInputs.Goal] = declared.ToString();
        }

        if (detail is not null)
        {
            inputs[AiAdaptationInputs.GoalDetail] = detail;
        }

        if (multiplier is not null)
        {
            inputs[AiAdaptationInputs.TargetMultiplier] = multiplier.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (targetYieldQuantity is not null)
        {
            inputs[AiAdaptationInputs.TargetYieldQuantity] =
                targetYieldQuantity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return inputs;
    }

    private async Task<SeededRecipe> SeedRecipeAsync()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var recipes = scope.ServiceProvider.GetRequiredService<IRecipeFacade>();

        var created = await recipes.CreateAsync(
            UserId,
            new CreateRecipeViewModel
            {
                Title = "Buttermilk Soda Bread",
                IngredientGroups =
                [
                    new RecipeIngredientGroupInputViewModel
                    {
                        Ingredients = [new RecipeIngredientInputViewModel { DisplayText = SelectedLine }],
                    },
                ],
            },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(created.Result.Succeeded, created.Result.Error?.Message);

        var snapshot = await recipes.GetSnapshotAsync(
            created.Result.Value!.RecipeId,
            created.Result.Value.VersionId,
            TestContext.Current.CancellationToken);

        var line = snapshot.Value!.Document.IngredientGroups.Single().Ingredients.Single();

        return new SeededRecipe(created.Result.Value.RecipeId, created.Result.Value.VersionId, line.Id);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner, "test-account");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    /// <summary>A scripted <see cref="IChatClient"/>: no network, no model, fully deterministic.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        private Func<string>? _always;

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public static FakeChatClient Returning(string body) => new() { _always = () => body };

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();

            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, _always!())) { ModelId = "test-model" });
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
}
