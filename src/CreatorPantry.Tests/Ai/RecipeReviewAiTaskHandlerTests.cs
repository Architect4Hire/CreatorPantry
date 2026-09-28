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
/// AIREC-006's task handler against a fake <see cref="IChatClient"/> and the real recipe module — no network,
/// no model. Covers what a valid answer becomes, the field-ref cross-check against the pinned snapshot (which
/// no evaluation fixture can reach, since those validate the document alone), and workspace isolation.
/// </summary>
public sealed class RecipeReviewAiTaskHandlerTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operation = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private const string IngredientLine = "400ml buttermilk, shaken";

    private static readonly PromptTemplate Template =
        EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly).Get(AiTaskCatalog.RecipeReview);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public RecipeReviewAiTaskHandlerTests()
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
    public async Task A_valid_answer_becomes_an_advisory_proposal_pinned_to_the_version()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(OrdinaryFinding(recipe))), recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        Assert.Equal(Template.OutputSchemaVersion, proposal.OutputSchemaVersion);
        Assert.Equal(Template.Id, proposal.PromptTemplateId);
        Assert.Equal(Template.BodyChecksum, proposal.PromptTemplateBodyChecksum);
        Assert.Equal(recipe.VersionId, proposal.SourceRecipeVersionId);
    }

    /// <summary>Nothing in an advisory proposal can reach the recipe, the same guarantee AIREC-004 carries.</summary>
    [Fact]
    public async Task No_row_in_the_proposal_can_be_applied_to_a_recipe()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(OrdinaryFinding(recipe))), recipe);

        Assert.All(outcome.Proposal!.Changes, change =>
        {
            Assert.Equal(AiChangeTargetKind.RecipeReviewFinding, change.TargetKind);
            Assert.False(AiChangeApplicability.IsApplicable(change.ChangeKind, change.TargetKind));
            Assert.Null(AiChangeTargetPolicy.For(change.TargetKind));
        });
    }

    [Fact]
    public async Task A_findings_fields_are_stored_under_the_names_a_reader_expects()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(OrdinaryFinding(recipe))), recipe);

        var add = Assert.Single(outcome.Proposal!.Changes, change => change.ChangeKind is AiChangeKind.Add);
        Assert.Equal("The chives are listed but never used in the method.", add.AfterValue);

        var fields = outcome.Proposal.Changes
            .Where(change => change.TargetId == add.TargetId && change.ChangeKind is AiChangeKind.Set)
            .ToDictionary(change => change.FieldName!, change => change.AfterValue);

        Assert.Equal("UnusedIngredient", fields[AiRecipeReviewFields.Category]);
        Assert.Equal("Minor", fields[AiRecipeReviewFields.Severity]);
        Assert.Equal("IngredientLine", fields[AiRecipeReviewFields.FieldKind]);
        Assert.Equal(recipe.IngredientId.ToString(), fields[AiRecipeReviewFields.FieldEntityId]);
    }

    /// <summary>The allergen/diet direction survives flattening, mirroring AIREC-004's own guarantee.</summary>
    [Fact]
    public async Task An_allergen_conflicts_direction_is_stored_intact()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(AllergenFinding(recipe))), recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);

        var fields = outcome.Proposal!.Changes
            .Where(change => change.ChangeKind is AiChangeKind.Set)
            .ToDictionary(change => change.FieldName!, change => change.AfterValue);

        Assert.Equal("tree nuts", fields[AiRecipeReviewFields.Allergen]);
        Assert.Equal("Introduces", fields[AiRecipeReviewFields.AllergenEffect]);

        var warning = Assert.Single(outcome.Proposal.Warnings);
        Assert.Equal(AiWarningKind.SafetyCaution, warning.Kind);
    }

    // ---- the field-ref cross-check against the pinned snapshot -------------------------------------------

    /// <summary>
    /// No evaluation fixture can reach this: <see cref="AiRecipeReviewOutputValidator"/> validates the document
    /// alone, with no snapshot to check an id against. This is the one path only the handler itself can prove.
    /// </summary>
    [Fact]
    public async Task A_finding_pointing_at_an_ingredient_line_outside_the_pinned_version_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var stale = Guid.NewGuid();
        var client = FakeChatClient.Returning(Answer($$"""
            "findings":[{"fieldRef":{"fieldKind":"IngredientLine","entityId":"{{stale}}"},
             "category":"UnusedIngredient","severity":"Minor",
             "summary":"Not used.","evidenceBasis":"CreatorSource","confidence":"High",
             "requiresReferenceCheck":false}]
            """));

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Null(outcome.Proposal);
    }

    [Fact]
    public async Task A_finding_pointing_at_a_step_outside_the_pinned_version_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var stale = Guid.NewGuid();
        var client = FakeChatClient.Returning(Answer($$"""
            "findings":[{"fieldRef":{"fieldKind":"Step","entityId":"{{stale}}"},
             "category":"AmbiguousStep","severity":"Minor",
             "summary":"Unclear.","evidenceBasis":"CreatorSource","confidence":"High",
             "requiresReferenceCheck":false}]
            """));

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    /// <summary>The real id, from the real pinned snapshot, is accepted — the check refuses a stale id, not every id.</summary>
    [Fact]
    public async Task A_finding_pointing_at_the_real_pinned_step_succeeds()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$"""
            "findings":[{"fieldRef":{"fieldKind":"Step","entityId":"{{recipe.StepId}}"},
             "category":"AmbiguousStep","severity":"Minor",
             "summary":"Could mean two things.","evidenceBasis":"CreatorSource","confidence":"High",
             "requiresReferenceCheck":false}]
            """));

        var outcome = await Run(client, recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
    }

    // ---- what reaches the prompt, and where --------------------------------------------------------------

    /// <summary>The recipe is the only creator content this capability carries, and it travels as SOURCE alone.</summary>
    [Fact]
    public async Task The_recipes_own_text_never_reaches_the_system_message()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer(OrdinaryFinding(recipe)));

        await Run(client, recipe);

        Assert.Contains(IngredientLine, UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain(IngredientLine, SystemMessage(client), StringComparison.Ordinal);
    }

    // ---- failure paths -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_operation_naming_no_recipe_is_refused()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer(OrdinaryFinding(recipe)));

        var outcome = await Run(client, recipe, recipeless: true);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    /// <summary>
    /// An allergen finding presented without its structured effect fails, the same guarantee AIREC-004 makes:
    /// the claim has nowhere to live except the closed enum, so leaving it out of the schema is a shape
    /// failure, not a lesser observation.
    /// </summary>
    [Fact]
    public async Task An_allergen_finding_missing_its_structured_effect_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$"""
            "findings":[{"fieldRef":{"fieldKind":"IngredientLine","entityId":"{{recipe.IngredientId}}"},
             "category":"AllergenConflict","severity":"Major",
             "summary":"Contains a tree-nut allergen.","evidenceBasis":"CreatorSource","confidence":"High",
             "requiresReferenceCheck":true}],
            "warnings":[{"kind":"SafetyCaution","message":"Check the label.","findingIndex":0}]
            """));

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// A worker running under workspace B, against an operation naming workspace A's recipe. The recipe is
    /// invisible through the facade, so the run fails before any provider call and none of A's content reaches
    /// a prompt.
    /// </summary>
    [Fact]
    public async Task A_recipe_in_another_workspace_is_invisible_to_the_handler()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer(OrdinaryFinding(recipe)));

        var outcome = await Run(client, recipe, workspaceId: WorkspaceB);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    /// <summary>And the failure discloses nothing about what it could not see.</summary>
    [Fact]
    public async Task A_cross_workspace_failure_discloses_nothing_about_the_recipe()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(
            FakeChatClient.Returning(Answer(OrdinaryFinding(recipe))), recipe, workspaceId: WorkspaceB);

        Assert.DoesNotContain("Buttermilk", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("soda bread", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recipe.RecipeId.ToString(), outcome.FailureSummary!, StringComparison.Ordinal);
    }

    // ---- answers -----------------------------------------------------------------------------------------

    private static string Answer(string body) => $$"""{"schemaVersion":"recipe.review.v1",{{body}}}""";

    private static string OrdinaryFinding(SeededRecipe recipe) => $$"""
        "findings":[{"fieldRef":{"fieldKind":"IngredientLine","entityId":"{{recipe.IngredientId}}"},
         "category":"UnusedIngredient","severity":"Minor",
         "summary":"The chives are listed but never used in the method.",
         "evidenceBasis":"CreatorSource","confidence":"High","requiresReferenceCheck":false}]
        """;

    private static string AllergenFinding(SeededRecipe recipe) => $$"""
        "findings":[{"fieldRef":{"fieldKind":"IngredientLine","entityId":"{{recipe.IngredientId}}"},
         "category":"AllergenConflict","severity":"Major",
         "summary":"Contains a tree-nut allergen not named elsewhere in the recipe.",
         "evidenceBasis":"CreatorSource","confidence":"High",
         "allergen":"tree nuts","allergenEffect":"Introduces","requiresReferenceCheck":true}],
        "warnings":[{"kind":"SafetyCaution","message":"Check the label.","findingIndex":0}]
        """;

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId, Guid IngredientId, Guid StepId);

    private static string UserMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.User).Text;

    private static string SystemMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.System).Text;

    private async Task<AiTaskHandlerOutcome> Run(
        FakeChatClient client,
        SeededRecipe recipe,
        bool recipeless = false,
        Guid? workspaceId = null)
    {
        const string pipelineKey = "test-ai-review";

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

        var handler = new RecipeReviewAiTaskHandler(
            gateway,
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly),
            new StoppedClock());

        var context = new AiTaskExecutionContext(
            Operation,
            workspaceId ?? WorkspaceA,
            LeaseToken: Guid.NewGuid(),
            AiOperationScope.Advisory,
            RecipeId: recipeless ? null : recipe.RecipeId,
            RecipeVersionId: recipeless ? null : recipe.VersionId,
            CorrelationId: Guid.NewGuid(),
            RenewLeaseAsync: _ => Task.CompletedTask);

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
                        Ingredients = [new RecipeIngredientInputViewModel { DisplayText = IngredientLine }],
                    },
                ],
                Instructions =
                [
                    new RecipeInstructionGroupInputViewModel
                    {
                        Steps = [new RecipeInstructionStepInputViewModel { Text = "Mix and bake at 220C." }],
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

        var document = snapshot.Value!.Document;
        var ingredientId = document.IngredientGroups.Single().Ingredients.Single().Id;
        var stepId = document.InstructionGroups.Single().Steps.Single().Id;

        return new SeededRecipe(created.Result.Value.RecipeId, created.Result.Value.VersionId, ingredientId, stepId);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner);

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
