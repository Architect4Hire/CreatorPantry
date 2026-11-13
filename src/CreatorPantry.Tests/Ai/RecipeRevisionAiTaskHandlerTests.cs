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
/// AIREC-003's task handler against a fake <see cref="IChatClient"/> and the real recipe module — no network, no
/// model. Covers what the model is told about its bound, that an answer inside the bound becomes a proposal, and
/// that the validator still refuses one outside it.
/// </summary>
/// <remarks>
/// <para>
/// Written after live runs of "Improve a recipe" failed on every revision observed: the bound was rendered as
/// <c>Recipe.title</c>, <c>Ingredient.displayText</c>, the model copied those strings into <c>fieldName</c>, and
/// the validator refused each answer as outside the scope. The validator was right and is unchanged; what the
/// model was told was wrong. The same harness as <c>RecipeAdaptationAiTaskHandlerTests</c>, for the same reason —
/// the bound is checked against a real serialized snapshot.
/// </para>
/// </remarks>
public sealed class RecipeRevisionAiTaskHandlerTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operation = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";
    private const string SelectedLine = "400ml buttermilk, shaken";

    private static readonly PromptTemplate Template =
        EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly).Get(AiTaskCatalog.RecipeRevision);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public RecipeRevisionAiTaskHandlerTests()
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

    // ---- what an answer becomes ----------------------------------------------------------------------------

    [Fact]
    public async Task An_answer_using_bare_field_names_becomes_a_proposal_pinned_to_the_version()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(
            FakeChatClient.Returning(IngredientNoteChange(recipe.IngredientId)), recipe, AiOperationScope.Ingredients);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        Assert.Equal(Template.OutputSchemaVersion, proposal.OutputSchemaVersion);
        Assert.Equal(recipe.VersionId, proposal.SourceRecipeVersionId);

        var change = Assert.Single(proposal.Changes);
        Assert.Equal(AiChangeTargetKind.Ingredient, change.TargetKind);
        Assert.Equal("preparationNote", change.FieldName);
    }

    /// <summary>
    /// The live answer, kept as the thing that must still be refused: a prefixed field name is not a field. Fixing
    /// what the model is told is the remedy, never widening what the validator accepts.
    /// </summary>
    [Fact]
    public async Task A_field_name_prefixed_with_its_target_is_still_refused()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(
            FakeChatClient.Returning(PrefixedFieldChange(recipe.IngredientId)), recipe, AiOperationScope.Ingredients);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Null(outcome.Proposal);
    }

    // ---- what the model is told ------------------------------------------------------------------------------

    [Fact]
    public async Task The_output_schema_pins_the_schema_version_the_validator_requires()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(IngredientNoteChange(recipe.IngredientId));

        await Run(client, recipe, AiOperationScope.Ingredients);

        Assert.Contains($"\"const\": \"{Template.OutputSchemaVersion}\"", SystemMessage(client), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_allowed_fields_are_rendered_as_bare_field_names_for_the_chosen_section_only()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(IngredientNoteChange(recipe.IngredientId));

        await Run(client, recipe, AiOperationScope.Ingredients);

        var system = SystemMessage(client);
        Assert.Contains("targetKind Ingredient with fieldName", system, StringComparison.Ordinal);
        Assert.Contains("\"preparationNote\"", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Ingredient.preparationNote", system, StringComparison.Ordinal);
        Assert.DoesNotContain("targetKind Recipe with", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_creators_goal_reaches_the_user_message_and_never_the_system_message()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(IngredientNoteChange(recipe.IngredientId));

        await Run(client, recipe, AiOperationScope.Ingredients, goal: "make the prep notes clearer");

        Assert.Contains("make the prep notes clearer", UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain("make the prep notes clearer", SystemMessage(client), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every scope's rendering, checked against the validator's own table: each field it permits is named bare, and
    /// none appears in the prefixed form a model would copy. The one prefixed name in the text is the example of
    /// what not to write.
    /// </summary>
    [Theory]
    [InlineData(AiOperationScope.WholeRecipe)]
    [InlineData(AiOperationScope.Ingredients)]
    [InlineData(AiOperationScope.Instructions)]
    [InlineData(AiOperationScope.Metadata)]
    public void Every_scope_names_each_permitted_field_bare_and_never_prefixed(AiOperationScope scope)
    {
        var rendered = AiScopeBounds.AllowedFields(scope);
        var targets = AiPolicy.AllowedTargets(scope).Where(target => AiChangeApplicability.For(target).Count > 0);

        foreach (var target in targets)
        {
            foreach (var field in AiPolicy.AllowedFields(scope, target))
            {
                Assert.Contains($"\"{field}\"", rendered, StringComparison.Ordinal);

                var prefixed = $"{target}.{field}";
                var expected = prefixed == "Recipe.title" ? 1 : 0;
                Assert.Equal(expected, CountOf(rendered, prefixed));
            }
        }
    }

    // ---- answers ---------------------------------------------------------------------------------------------

    private static string IngredientNoteChange(Guid ingredientId) => $$"""
        {"schemaVersion":"recipe.revision.v1",
         "changes":[{"changeKind":"Set","targetKind":"Ingredient","targetId":"{{ingredientId}}","fieldName":"preparationNote","afterValue":"shaken well"}],
         "warnings":[{"kind":"Rationale","message":"Says how to prepare it in the note rather than the line.","changeIndex":0}]}
        """;

    /// <summary>The shape a live model returned after being told the allowed fields were "Ingredient.preparationNote".</summary>
    private static string PrefixedFieldChange(Guid ingredientId) => $$"""
        {"schemaVersion":"recipe.revision.v1",
         "changes":[{"changeKind":"Set","targetKind":"Ingredient","targetId":"{{ingredientId}}","fieldName":"Ingredient.preparationNote","afterValue":"shaken well"}]}
        """;

    // ---- helpers ---------------------------------------------------------------------------------------------

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId, Guid IngredientId);

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string UserMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.User).Text;

    private static string SystemMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.System).Text;

    private async Task<AiTaskHandlerOutcome> Run(
        FakeChatClient client,
        SeededRecipe recipe,
        AiOperationScope scope,
        string? goal = null)
    {
        const string pipelineKey = "test-ai-revision";

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

        using var serviceScope = _provider.CreateScope();
        Resolve(serviceScope, WorkspaceA);

        var handler = new RecipeRevisionAiTaskHandler(
            gateway,
            serviceScope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly),
            new StoppedClock());

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (goal is not null)
        {
            inputs[AiRevisionInputs.Goal] = goal;
        }

        var context = new AiTaskExecutionContext(
            Operation,
            WorkspaceA,
            LeaseToken: Guid.NewGuid(),
            scope,
            RecipeId: recipe.RecipeId,
            RecipeVersionId: recipe.VersionId,
            CorrelationId: Guid.NewGuid(),
            RenewLeaseAsync: _ => Task.CompletedTask,
            Inputs: inputs);

        return await handler.HandleAsync(context, TestContext.Current.CancellationToken);
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
