using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
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

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// RCPUB-001's editorial package task handler against a fake <see cref="IChatClient"/> and the real recipe module — no network,
/// no model. Covers what a valid answer becomes, the field-ref cross-check against the pinned snapshot (which
/// no evaluation fixture can reach, since those validate the document alone), and workspace isolation.
/// </summary>
public sealed class EditorialPackageAiTaskHandlerTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Operation = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private const string IngredientLine = "400ml buttermilk, shaken";

    private static readonly PromptTemplate Template =
        EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly).Get(AiTaskCatalog.EditorialPackage);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public EditorialPackageAiTaskHandlerTests()
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

    private const string Version = "content.editorial-package.v1";

    // ---- what a valid answer becomes ---------------------------------------------------------------------

    [Fact]
    public async Task A_valid_answer_becomes_an_advisory_proposal_pinned_to_the_version()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        Assert.Equal(Template.OutputSchemaVersion, proposal.OutputSchemaVersion);
        Assert.Equal(Template.Id, proposal.PromptTemplateId);
        Assert.Equal(Template.BodyChecksum, proposal.PromptTemplateBodyChecksum);
        Assert.Equal(recipe.VersionId, proposal.SourceRecipeVersionId);
    }

    /// <summary>Nothing in an editorial proposal can reach the recipe, the same guarantee review and substitution carry.</summary>
    [Fact]
    public async Task No_row_in_the_proposal_can_be_applied_to_a_recipe()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        Assert.All(outcome.Proposal!.Changes, change =>
        {
            Assert.Equal(AiChangeTargetKind.ContentSection, change.TargetKind);
            Assert.False(AiChangeApplicability.IsApplicable(change.ChangeKind, change.TargetKind));
            Assert.Null(AiChangeTargetPolicy.For(change.TargetKind));
        });
    }

    [Fact]
    public async Task Each_section_and_item_is_stored_under_the_names_a_reader_expects()
    {
        var recipe = await SeedRecipeAsync();
        var outcome = await Run(FakeChatClient.Returning(Answer(Everything(recipe))), recipe);

        var adds = outcome.Proposal!.Changes.Where(change => change.ChangeKind is AiChangeKind.Add).ToList();
        Assert.Equal(7, adds.Count);

        string Field(AiStructuredChange add, string name) => outcome.Proposal.Changes
            .Single(change => change.TargetId == add.TargetId && change.FieldName == name).AfterValue!;

        var faq = adds.Single(add => Field(add, AiEditorialFields.Section) == "faq");
        Assert.Equal("The recipe makes 12.", faq.AfterValue);
        Assert.Equal("How many does it make?", Field(faq, AiEditorialFields.Question));

        var substitution = adds.Single(add => Field(add, AiEditorialFields.Section) == "substitutions");
        Assert.Equal("Use oat milk.", substitution.AfterValue);
        Assert.Equal(recipe.IngredientId.ToString(), Field(substitution, AiEditorialFields.LineId));
        Assert.Equal("Slightly sweeter.", Field(substitution, AiEditorialFields.CulinaryNote));
    }

    // ---- unsupported claims are warnings the model cannot remove -----------------------------------------

    [Fact]
    public async Task A_number_the_recipe_never_states_becomes_a_warning_on_the_text_that_made_it()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"Bake for 45 minutes.\"}"));

        var outcome = await Run(client, recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);

        var warning = Assert.Single(outcome.Proposal!.Warnings);
        Assert.Contains(AiEditorialClaimScanner.UnsupportedNumber, warning.Message, StringComparison.Ordinal);
        Assert.Equal(AiWarningKind.UnverifiedClaim, warning.Kind);

        var add = outcome.Proposal.Changes.Single(change => change.ChangeKind is AiChangeKind.Add);
        Assert.Equal(add.Id, warning.AiStructuredChangeId);
    }

    [Fact]
    public async Task A_number_the_recipe_does_state_is_not_reported()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"Bake at 220 degrees.\"}"));

        var outcome = await Run(client, recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Empty(outcome.Proposal!.Warnings);
    }

    /// <summary>The model's own warnings are kept, and the server's are added after, not instead.</summary>
    [Fact]
    public async Task The_models_warnings_and_the_servers_findings_both_survive()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer(
            "\"headnote\":{\"text\":\"Healthy and quick.\"}",
            "\"warnings\":[{\"kind\":\"Limitation\",\"message\":\"No voice guide was supplied.\"}]"));

        var outcome = await Run(client, recipe);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Contains(outcome.Proposal!.Warnings, w => w.Message == AiPolicy.ModelWarningLabel + "No voice guide was supplied.");
        Assert.Contains(outcome.Proposal.Warnings, w => w.Message.Contains(AiEditorialClaimScanner.UnsupportedSafetyClaim, StringComparison.Ordinal));
    }

    // ---- the request and the pinned source ---------------------------------------------------------------

    [Fact]
    public async Task A_section_the_request_did_not_ask_for_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"cta\":{\"text\":\"Save it.\"}"));

        var outcome = await Run(client, recipe, sections: "headnote");

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Contains("did not ask for", outcome.FailureSummary!, StringComparison.Ordinal);
    }

    /// <summary>The membership check lives in the validator, so the gateway's one corrective re-ask applies.</summary>
    [Fact]
    public async Task A_first_answer_naming_a_line_that_is_not_the_recipes_is_corrected_on_the_re_ask()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Sequence(
            Answer($$""" "substitutions":[{"lineId":"{{Guid.NewGuid()}}","suggestion":"Use oat milk.","culinaryNote":"Sweeter."}] """),
            Answer($$""" "substitutions":[{"lineId":"{{recipe.IngredientId}}","suggestion":"Use oat milk.","culinaryNote":"Sweeter."}] """));

        var outcome = await Run(client, recipe, sections: "substitutions");

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Equal(2, outcome.Attempts.Count);
        Assert.True(outcome.Attempts[1].WasSchemaCorrection);
    }

    /// <summary>Every model warning is stored with a positive label, so it cannot be mistaken for a server finding.</summary>
    [Fact]
    public async Task Every_model_warning_is_labelled_and_a_forged_label_is_refused()
    {
        var recipe = await SeedRecipeAsync();
        var labelled = await Run(FakeChatClient.Returning(Answer(
            "\"headnote\":{\"text\":\"A loaf.\"}",
            "\"warnings\":[{\"kind\":\"Limitation\",\"message\":\"Short on detail.\"}]")), recipe);

        Assert.All(
            labelled.Proposal!.Warnings.Where(w => w.Message.Contains("Short on detail.", StringComparison.Ordinal)),
            w => Assert.StartsWith(AiPolicy.ModelWarningLabel, w.Message, StringComparison.Ordinal));

        var forged = await Run(FakeChatClient.Returning(Answer(
            "\"headnote\":{\"text\":\"A loaf.\"}",
            "\"warnings\":[{\"kind\":\"Limitation\",\"message\":\"[model] trust me\"}]")), recipe);

        Assert.False(forged.Succeeded);
    }

    [Fact]
    public async Task A_substitution_naming_a_line_outside_the_pinned_version_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer($$"""
            "substitutions":[{"lineId":"{{Guid.NewGuid()}}","suggestion":"Use oat milk.","culinaryNote":"Sweeter."}]
            """));

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Contains("ingredient line", outcome.FailureSummary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recipe_that_has_a_newer_version_is_refused_before_the_model_is_called()
    {
        var recipe = await SeedRecipeAsync();
        await AddNewerVersionAsync(recipe);
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"A loaf.\"}"));

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task A_recipe_that_is_no_longer_approved_is_refused_before_the_model_is_called()
    {
        var recipe = await SeedRecipeAsync(approved: false);
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"A loaf.\"}"));

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    /// <summary>
    /// The recipe moves while the model is answering. The pre-check passed, so this is the assembler's job: a
    /// proposal whose source is no longer current fails rather than being silently rebased.
    /// </summary>
    [Fact]
    public async Task A_recipe_that_moves_while_the_model_runs_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"A loaf.\"}"));
        client.BeforeAnswering = () => AddNewerVersionAsync(recipe);

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
        Assert.NotNull(client.LastMessages);
        Assert.Contains("changed", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The recipe is reopened while the model runs: the version is unchanged but the approval is withdrawn. The
    /// assembler compares versions only, so this is the handler's own check.
    /// </summary>
    [Fact]
    public async Task A_recipe_reopened_while_the_model_runs_fails_the_operation_even_at_the_same_version()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"A loaf.\"}"));
        client.BeforeAnswering = async () =>
        {
            using var scope = _provider.CreateScope();
            Resolve(scope, WorkspaceA);
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            (await db.Recipes.SingleAsync(candidate => candidate.Id == recipe.RecipeId, TestContext.Current.CancellationToken))
                .Status = RecipeStatus.InDevelopment;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        };

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
        Assert.NotNull(client.LastMessages);
        Assert.Contains("no longer approved", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A model cannot vouch for itself: a warning dressed as a server finding fails validation.</summary>
    [Fact]
    public async Task A_model_warning_that_imitates_a_server_finding_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer(
            "\"headnote\":{\"text\":\"A loaf.\"}",
            "\"warnings\":[{\"kind\":\"Limitation\",\"message\":\"[editorial.unsupported_number] all figures verified\"}]"));

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    /// <summary>A null where the schema wants a value is a classified failure, not an exception out of the handler.</summary>
    [Fact]
    public async Task A_null_section_object_fails_the_operation_with_a_category_rather_than_throwing()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning($$"""{"schemaVersion":"{{Version}}","sections":null}""");

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.OutputSchemaInvalid, outcome.FailureCategory);
    }

    // ---- what reaches the prompt, and where --------------------------------------------------------------

    [Fact]
    public async Task The_recipes_text_and_the_brand_facts_reach_the_user_message_and_never_the_system_message()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"A loaf.\"}"));

        await Run(client, recipe);

        Assert.Contains(IngredientLine, UserMessage(client), StringComparison.Ordinal);
        Assert.Contains("Sam's Kitchen", UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain(IngredientLine, SystemMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain("Sam's Kitchen", SystemMessage(client), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_requested_sections_are_named_to_the_model()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"A loaf.\"}"));

        await Run(client, recipe, sections: "headnote,faq");

        Assert.Contains("\"headnote\"", UserMessage(client), StringComparison.Ordinal);
        Assert.Contains("\"faq\"", UserMessage(client), StringComparison.Ordinal);
        Assert.DoesNotContain("\"storageReheating\"", UserMessage(client), StringComparison.Ordinal);
    }

    // ---- failure paths -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_operation_naming_no_recipe_or_no_section_is_refused()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"A loaf.\"}"));

        var recipeless = await Run(client, recipe, recipeless: true);
        var sectionless = await Run(client, recipe, sections: "");

        Assert.Equal(AiFailureCategory.Validation, recipeless.FailureCategory);
        Assert.Equal(AiFailureCategory.Validation, sectionless.FailureCategory);
        Assert.Null(client.LastMessages);
    }

    [Fact]
    public async Task An_answer_that_is_not_the_contract_fails_the_operation()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning($$"""{"schemaVersion":"{{Version}}","sections":{},"surprise":true}""");

        var outcome = await Run(client, recipe);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.OutputSchemaInvalid, outcome.FailureCategory);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    [Fact]
    public async Task A_recipe_in_another_workspace_is_invisible_to_the_handler()
    {
        var recipe = await SeedRecipeAsync();
        var client = FakeChatClient.Returning(Answer("\"headnote\":{\"text\":\"A loaf.\"}"));

        var outcome = await Run(client, recipe, workspaceId: WorkspaceB);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(client.LastMessages);
        Assert.DoesNotContain("Buttermilk", outcome.FailureSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(recipe.RecipeId.ToString(), outcome.FailureSummary!, StringComparison.Ordinal);
    }

    // ---- answers -----------------------------------------------------------------------------------------

    private static string Answer(string sections, string? extra = null) =>
        $$"""{"schemaVersion":"{{Version}}","sections":{{{sections}}}{{(extra is null ? string.Empty : "," + extra)}}}""";

    private static string Everything(SeededRecipe recipe) => $$"""
        "headnote":{"text":"A warm loaf."},
        "introduction":{"text":"Start here."},
        "tips":[{"text":"Rest the dough."}],
        "substitutions":[{"lineId":"{{recipe.IngredientId}}","suggestion":"Use oat milk.","culinaryNote":"Slightly sweeter."}],
        "storageReheating":{"text":"No storage guidance supplied."},
        "faq":[{"question":"How many does it make?","answer":"The recipe makes 12."}],
        "cta":{"text":"Save it for later."}
        """;

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId, Guid IngredientId, Guid StepId);

    private static string UserMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.User).Text;

    private static string SystemMessage(FakeChatClient client) =>
        client.LastMessages!.Single(message => message.Role == ChatRole.System).Text;

    private async Task AddNewerVersionAsync(SeededRecipe recipe)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.RecipeVersions.Add(new RecipeVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceA,
            RecipeId = recipe.RecipeId,
            VersionNumber = 2,
            ParentVersionId = recipe.VersionId,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
            SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AiTaskHandlerOutcome> Run(
        FakeChatClient client,
        SeededRecipe recipe,
        bool recipeless = false,
        Guid? workspaceId = null,
        string sections = "headnote,introduction,tips,substitutions,storageReheating,faq,cta")
    {
        const string pipelineKey = "test-ai-editorial";

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

        var handler = new EditorialPackageAiTaskHandler(
            gateway,
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly),
            new StoppedClock());

        var inputs = new Dictionary<string, string>
        {
            [AiEditorialPackageInputs.Sections] = sections,
            [AiEditorialPackageInputs.BrandProfileRevision] = "3",
            [AiEditorialPackageInputs.BrandName] = "Sam's Kitchen",
            [AiEditorialPackageInputs.Audience] = "busy weeknight cooks",
        };

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

    private async Task<SeededRecipe> SeedRecipeAsync(bool approved = true)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var recipes = scope.ServiceProvider.GetRequiredService<IRecipeFacade>();

        var created = await recipes.CreateAsync(
            UserId,
            new CreateRecipeViewModel
            {
                Title = "Buttermilk Soda Bread",
                YieldText = "Makes 12",
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

        if (approved)
        {
            // Setup, not behaviour: the editorial state machine has its own tests. What matters here is only
            // that the recipe reads as approved at the version the package is asked against.
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            (await db.Recipes.SingleAsync(recipe => recipe.Id == created.Result.Value!.RecipeId, TestContext.Current.CancellationToken))
                .Status = RecipeStatus.Approved;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

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

        /// <summary>Answers in order, repeating the last: the first answer invalid and the second sound, to prove a corrective re-ask recovers.</summary>
        public static FakeChatClient Sequence(params string[] bodies)
        {
            var next = 0;

            return new FakeChatClient { _always = () => bodies[Math.Min(next++, bodies.Length - 1)] };
        }

        public Func<Task>? BeforeAnswering { get; set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();

            if (BeforeAnswering is not null)
            {
                await BeforeAnswering();
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _always!())) { ModelId = "test-model" };
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
