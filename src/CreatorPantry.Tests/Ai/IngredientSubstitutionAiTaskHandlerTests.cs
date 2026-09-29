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
/// AIREC-004's task handler against a fake <see cref="IChatClient"/> and the real recipe module — no network,
/// no model. Covers what reaches the prompt and where, how a validated answer becomes advisory rows, and the
/// failure paths.
/// </summary>
/// <remarks>
/// The recipe module is real rather than stubbed because the containment claims are about a real serialized
/// snapshot. A fake facade returning a hand-built document would let the test agree with itself about what
/// the creator's own words look like in the envelope, which is the one thing worth checking.
/// </remarks>
public sealed class IngredientSubstitutionAiTaskHandlerTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operation = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    /// <summary>The creator's own wording, distinctive enough that its presence anywhere is unambiguous.</summary>
    private const string SelectedLine = "400ml buttermilk, shaken";

    private static readonly PromptTemplate Template =
        EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly).Get(AiTaskCatalog.IngredientSubstitution);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public IngredientSubstitutionAiTaskHandlerTests()
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
        var client = FakeChatClient.Returning(TwoAlternatives);

        var outcome = await Run(client, recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        Assert.Equal(Template.OutputSchemaVersion, proposal.OutputSchemaVersion);
        Assert.Equal(Template.Id, proposal.PromptTemplateId);
        Assert.Equal(Template.BodyChecksum, proposal.PromptTemplateBodyChecksum);
        Assert.Equal(recipe.VersionId, proposal.SourceRecipeVersionId);
    }

    /// <summary>
    /// Nothing in an advisory proposal can reach the recipe: every row's target is one the applicability
    /// table does not cover and the recipe module's vocabulary cannot express.
    /// </summary>
    [Fact]
    public async Task No_row_in_the_proposal_can_be_applied_to_a_recipe()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(TwoAlternatives), recipe);

        Assert.All(outcome.Proposal!.Changes, change =>
        {
            Assert.Equal(AiChangeTargetKind.IngredientSubstitution, change.TargetKind);
            Assert.False(AiChangeApplicability.IsApplicable(change.ChangeKind, change.TargetKind));
            Assert.Null(AiChangeTargetPolicy.For(change.TargetKind));
        });
    }

    /// <summary>The rank survives as the Add row's position: the model put them in an order deliberately.</summary>
    [Fact]
    public async Task Each_alternative_becomes_an_add_row_carrying_its_rank()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(TwoAlternatives), recipe);

        var adds = outcome.Proposal!.Changes
            .Where(change => change.ChangeKind is AiChangeKind.Add)
            .OrderBy(change => change.SortOrder)
            .ToList();

        Assert.Equal(2, adds.Count);
        Assert.Equal("soured milk", adds[0].AfterValue);
        Assert.Equal(0, adds[0].ProposedPosition);
        Assert.Equal("plain yoghurt let down with water", adds[1].AfterValue);
        Assert.Equal(1, adds[1].ProposedPosition);
        Assert.NotEqual(adds[0].TargetId, adds[1].TargetId);
    }

    [Fact]
    public async Task An_alternatives_guidance_is_stored_under_the_names_a_reader_expects()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(TwoAlternatives), recipe);

        var first = outcome.Proposal!.Changes
            .Where(change => change.ChangeKind is AiChangeKind.Add)
            .OrderBy(change => change.SortOrder)
            .First();

        var fields = outcome.Proposal.Changes
            .Where(change => change.TargetId == first.TargetId && change.ChangeKind is AiChangeKind.Set)
            .ToDictionary(change => change.FieldName!, change => change.AfterValue);

        Assert.Equal("the acid that activates the bicarbonate", fields[AiSubstitutionFields.FunctionalRole]);
        Assert.Equal("the same volume", fields[AiSubstitutionFields.QuantityGuidance]);
        Assert.Equal("Moderate", fields[AiSubstitutionFields.Confidence]);
        Assert.Equal("ModelKnowledge", fields[AiSubstitutionFields.EvidenceBasis]);
        Assert.Equal("bake two and check the rise", fields[AiSubstitutionFields.TestRecommendation]);
    }

    /// <summary>
    /// The direction survives flattening, which is the part that matters: every phrase storage can produce
    /// names an effect that runs one way, because the enum has no member that runs the other. It is stored as
    /// words rather than as the enum name, because this is the field where being understood matters most.
    /// </summary>
    [Fact]
    public async Task An_allergen_effect_is_stored_in_words_with_its_direction_intact()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(AllergenAlternative), recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);

        var effects = outcome.Proposal!.Changes
            .Single(change => change.FieldName == AiSubstitutionFields.AllergenEffects)
            .AfterValue;

        Assert.Equal("introduces tree nuts", effects);
    }

    /// <inheritdoc cref="An_allergen_effect_is_stored_in_words_with_its_direction_intact"/>
    [Fact]
    public async Task A_dietary_effect_is_stored_in_words_with_its_direction_intact()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(DietaryAlternative), recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);

        var effects = outcome.Proposal!.Changes
            .Single(change => change.FieldName == AiSubstitutionFields.DietaryEffects)
            .AfterValue;

        Assert.Equal("may conflict with kosher", effects);
    }

    /// <summary>A warning about one alternative is re-addressed to that alternative's own row.</summary>
    [Fact]
    public async Task A_warning_about_one_alternative_lands_on_that_alternative()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(AllergenAlternative), recipe);

        var add = outcome.Proposal!.Changes.Single(change => change.ChangeKind is AiChangeKind.Add);
        var warning = Assert.Single(outcome.Proposal.Warnings);

        Assert.Equal(AiWarningKind.SafetyCaution, warning.Kind);
        Assert.Equal(add.Id, warning.AiStructuredChangeId);
    }

    /// <summary>
    /// The no-alternative answer. It produces no rows at all and still succeeds, carrying the explanation as
    /// a warning about the answer as a whole.
    /// </summary>
    [Fact]
    public async Task An_answer_with_no_alternative_is_a_proposal_of_warnings_alone()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(NoAlternative), recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Empty(outcome.Proposal!.Changes);

        var warning = Assert.Single(outcome.Proposal.Warnings);
        Assert.Null(warning.AiStructuredChangeId);
    }

    // ---- what reaches the prompt, and where --------------------------------------------------------------

    /// <summary>
    /// The creator's own ingredient wording is untrusted content. It belongs in the fenced source, in the user
    /// message — never in the system message, where a model reads instructions.
    /// </summary>
    [Fact]
    public async Task The_creators_ingredient_text_never_reaches_the_system_message()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        await Run(client, recipe);

        Assert.Contains(SelectedLine, UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain(SelectedLine, SystemMessage(client), StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason is the field a creator types an allergy into. It travels as preference text in the user
    /// message and is never folded into the task's instructions.
    /// </summary>
    [Fact]
    public async Task The_creators_reason_never_reaches_the_system_message()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        await Run(client, recipe, reason: "my reader cannot have dairy");

        Assert.Contains("my reader cannot have dairy", UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain("my reader cannot have dairy", SystemMessage(client), StringComparison.Ordinal);
    }

    /// <summary>
    /// The line is named by position. The id would be safe from an injection standpoint and is still absent:
    /// ai.md asks that a model not be handed identifiers, and this document gives it nothing to do with one.
    /// </summary>
    [Fact]
    public async Task The_selected_line_is_named_by_position_and_not_by_id()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        await Run(client, recipe);

        var system = SystemMessage(client);

        Assert.Contains("line 1 of ingredient group 1", system, StringComparison.Ordinal);
        Assert.DoesNotContain(recipe.IngredientId.ToString(), system, StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent reason reads as a complete sentence, and one that says not to guess. A blank would invite the
    /// model to supply a dietary motive the creator never stated.
    /// </summary>
    [Fact]
    public async Task An_absent_reason_says_so_rather_than_leaving_a_blank()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        await Run(client, recipe, reason: null);

        Assert.Contains("no reason was stated", UserMessage(client), StringComparison.Ordinal);
    }

    /// <summary>The bound the server enforces is the bound the model is told, taken from the same constant.</summary>
    [Fact]
    public async Task The_model_is_told_the_limit_the_validator_applies()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        await Run(client, recipe);

        Assert.Contains(
            $"at most {AiPolicy.MaxSubstitutionCount} alternatives",
            SystemMessage(client),
            StringComparison.Ordinal);
    }

    // ---- failure paths -----------------------------------------------------------------------------------

    /// <summary>
    /// The request seam checks this too. It is checked again because the two run in different processes, with
    /// a JSON dictionary and no shared type between them.
    /// </summary>
    [Fact]
    public async Task An_ingredient_that_is_not_in_the_pinned_version_fails_without_calling_the_model()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        var outcome = await Run(client, recipe with { IngredientId = Guid.NewGuid() });

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task An_operation_naming_no_ingredient_fails_without_calling_the_model()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        var outcome = await Run(client, recipe, inputs: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task An_operation_naming_no_recipe_is_refused()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        var outcome = await Run(client, recipe, recipeless: true);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// A worker running under workspace B, against an operation naming workspace A's recipe. The recipe is
    /// invisible through the facade, so the run fails before any provider call and none of A's content
    /// reaches a prompt.
    /// </summary>
    /// <remarks>
    /// tenancy.md asks for the two-workspace proof on background processing and AI retrieval, not only on the
    /// request seam — and this is the capability where the content in question is a whole recipe and the
    /// creator's stated reason for asking about it.
    /// </remarks>
    [Fact]
    public async Task A_recipe_in_another_workspace_is_invisible_to_the_handler()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(TwoAlternatives);

        var outcome = await Run(client, recipe, workspaceId: WorkspaceB);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    /// <summary>
    /// And the failure says nothing about what it could not see — not the title, not the ingredient, not
    /// whether the recipe exists at all.
    /// </summary>
    [Fact]
    public async Task A_cross_workspace_failure_discloses_nothing_about_the_recipe()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(FakeChatClient.Returning(TwoAlternatives), recipe, workspaceId: WorkspaceB);

        Assert.DoesNotContain("Buttermilk", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("soda bread", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recipe.RecipeId.ToString(), outcome.FailureSummary!, StringComparison.Ordinal);
    }

    // ---- failure paths -----------------------------------------------------------------------------------

    /// <summary>
    /// A domain-invalid answer fails and stores nothing. High confidence on evidence the answer itself calls
    /// unknown is the case AIREC-004's restriction names, so it is the one worth taking end to end.
    /// </summary>
    [Fact]
    public async Task An_answer_claiming_confidence_it_cannot_support_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(FakeChatClient.Returning(UnevidencedHighConfidence), recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Null(outcome.Proposal);
    }

    /// <summary>The provider's own words never become the failure summary a creator or a log could read.</summary>
    [Fact]
    public async Task A_failure_summary_carries_none_of_the_answer()
    {
        var recipe = await SeedRecipeAsync();

        var outcome = await Run(FakeChatClient.Returning(AllergenUncautioned), recipe);

        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("almond", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- answers -----------------------------------------------------------------------------------------

    private static string Answer(string body) =>
        $$"""{"schemaVersion":"recipe.substitution.v1",{{body}}}""";

    private const string TwoAlternatives = """
        "substitutions":[
          {"rank":1,"alternative":"soured milk",
           "functionalRole":"the acid that activates the bicarbonate",
           "quantityGuidance":"the same volume","flavorImpact":"slightly less tangy",
           "confidence":"Moderate","evidenceBasis":"ModelKnowledge",
           "testRecommendation":"bake two and check the rise"},
          {"rank":2,"alternative":"plain yoghurt let down with water",
           "functionalRole":"the same acid, carried in something thicker",
           "quantityGuidance":"three parts yoghurt to one of water",
           "confidence":"Low","evidenceBasis":"Unknown",
           "testRecommendation":"make a quarter batch first"}
        ]
        """;

    private const string AllergenAlternative = """
        "substitutions":[
          {"rank":1,"alternative":"almond milk, soured",
           "functionalRole":"the acid that activates the bicarbonate",
           "quantityGuidance":"the same volume",
           "allergenEffects":[{"allergen":"tree nuts","effect":"Introduces"}],
           "confidence":"Moderate","evidenceBasis":"ModelKnowledge",
           "testRecommendation":"bake one and check the crumb"}
        ],
        "warnings":[{"kind":"SafetyCaution","message":"Check the label, and check with whoever is eating.","substitutionIndex":0}]
        """;

    private const string DietaryAlternative = """
        "substitutions":[
          {"rank":1,"alternative":"soured milk",
           "functionalRole":"the acid that activates the bicarbonate",
           "quantityGuidance":"the same volume",
           "dietaryEffects":[{"diet":"kosher","effect":"MayConflict"}],
           "confidence":"Moderate","evidenceBasis":"ModelKnowledge",
           "testRecommendation":"bake one and check the crumb"}
        ],
        "warnings":[{"kind":"SafetyCaution","message":"Check with whoever keeps the diet.","substitutionIndex":0}]
        """;

    private const string AllergenUncautioned = """
        "substitutions":[
          {"rank":1,"alternative":"almond milk, soured",
           "functionalRole":"the acid that activates the bicarbonate",
           "quantityGuidance":"the same volume",
           "allergenEffects":[{"allergen":"tree nuts","effect":"Introduces"}],
           "confidence":"Moderate","evidenceBasis":"ModelKnowledge",
           "testRecommendation":"bake one and check the crumb"}
        ]
        """;

    private const string UnevidencedHighConfidence = """
        "substitutions":[
          {"rank":1,"alternative":"soured milk",
           "functionalRole":"the acid that activates the bicarbonate",
           "quantityGuidance":"the same volume",
           "confidence":"High","evidenceBasis":"Unknown",
           "testRecommendation":"bake two and check the rise"}
        ]
        """;

    private const string NoAlternative = """
        "substitutions":[],
        "warnings":[{"kind":"SafetyCaution","message":"The salt here is curing the pork, not seasoning it."}]
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
        string? reason = "I am out of buttermilk",
        IReadOnlyDictionary<string, string>? inputs = null,
        bool recipeless = false,
        Guid? workspaceId = null)
    {
        const string pipelineKey = "test-ai-substitution";

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

        var handler = new IngredientSubstitutionAiTaskHandler(
            gateway,
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly),
            new StoppedClock());

        inputs ??= Inputs(recipe.IngredientId, reason);

        var context = new AiTaskExecutionContext(
            Operation,
            workspaceId ?? WorkspaceA,
            LeaseToken: Guid.NewGuid(),
            AiOperationScope.Advisory,
            RecipeId: recipeless ? null : recipe.RecipeId,
            RecipeVersionId: recipeless ? null : recipe.VersionId,
            CorrelationId: Guid.NewGuid(),
            RenewLeaseAsync: _ => Task.CompletedTask,
            Inputs: inputs);

        return await handler.HandleAsync(context, TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Inputs(Guid ingredientId, string? reason)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AiSubstitutionInputs.IngredientId] = ingredientId.ToString(),
        };

        if (reason is not null)
        {
            inputs[AiSubstitutionInputs.Reason] = reason;
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

        public static FakeChatClient Returning(string body) =>
            new() { _always = () => Answer(body) };

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
