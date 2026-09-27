using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Polly.Timeout;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-001's task handler against a fake <see cref="IChatClient"/> -- no network, no model. Covers rendering
/// the brief, translating a validated <see cref="AiConceptOutputDocument"/> into a proposal with no source
/// recipe, and the failure paths a malformed or under-count answer takes.
/// </summary>
public sealed class RecipeConceptsAiTaskHandlerTests
{
    private static readonly Guid Workspace = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Operation = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly PromptTemplate Template =
        EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly).Get(AiTaskCatalog.RecipeConcepts);

    // ---- success -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_answer_becomes_a_proposal_with_one_add_and_several_set_rows_per_concept()
    {
        var client = FakeChatClient.Returning(TwoConcepts);
        var outcome = await Run(client, inputs: new Dictionary<string, string> { ["cuisine"] = "Sichuan" });

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        Assert.Equal(Template.OutputSchemaVersion, proposal.OutputSchemaVersion);
        Assert.Equal(Template.Id, proposal.PromptTemplateId);
        Assert.Equal(Template.BodyChecksum, proposal.PromptTemplateBodyChecksum);
        Assert.Null(proposal.SourceRecipeVersionId);

        // Concept 1: an Add (title) plus five Set rows -- summary, rationale, assumptions, ingredients, none
        // of the two optional fields supplied. Concept 2 supplies every field including the two optional ones.
        var adds = proposal.Changes.Where(change => change.ChangeKind is AiChangeKind.Add).ToList();
        Assert.Equal(2, adds.Count);
        Assert.All(adds, add => Assert.Equal(AiChangeTargetKind.RecipeConcept, add.TargetKind));
        Assert.Equal("Mapo Tofu Reimagined", adds[0].AfterValue);
        Assert.Equal(0, adds[0].ProposedPosition);
        Assert.Equal("Dan Dan Noodle Bowl", adds[1].AfterValue);
        Assert.Equal(1, adds[1].ProposedPosition);

        var concept1Sets = proposal.Changes
            .Where(change => change.ChangeKind is AiChangeKind.Set && change.TargetId == adds[0].TargetId)
            .ToList();
        Assert.Equal(4, concept1Sets.Count);
        Assert.Contains(concept1Sets, set => set.FieldName == "summary" && set.AfterValue == "A spicier take.");
        Assert.DoesNotContain(concept1Sets, set => set.FieldName == "timeBudgetNote");
        Assert.DoesNotContain(concept1Sets, set => set.FieldName == "skillLevelFit");

        var concept2Sets = proposal.Changes
            .Where(change => change.ChangeKind is AiChangeKind.Set && change.TargetId == adds[1].TargetId)
            .ToList();
        Assert.Contains(concept2Sets, set => set.FieldName == "timeBudgetNote" && set.AfterValue == "Fits a 30 minute budget.");
        Assert.Contains(concept2Sets, set => set.FieldName == "skillLevelFit" && set.AfterValue == "Beginner friendly.");

        // No before value: neither concept exists anywhere for the server to have read one from.
        Assert.All(proposal.Changes, change => Assert.Null(change.BeforeValue));

        // Every change is bounded to this workspace and starts undecided.
        Assert.All(proposal.Changes, change =>
        {
            Assert.Equal(Workspace, change.WorkspaceId);
            Assert.Equal(AiChangeDisposition.Pending, change.Disposition);
        });
    }

    /// <summary>
    /// The brief carries the creator's own supplied values, as a PREFERENCES (creator-data) segment in the
    /// user message -- not folded into the task's own instructions in the system message.
    /// </summary>
    [Fact]
    public async Task Supplied_inputs_render_into_the_preferences_segment_of_the_user_message()
    {
        var client = FakeChatClient.Returning(TwoConcepts);

        await Run(client, inputs: new Dictionary<string, string> { ["cuisine"] = "Sichuan", ["skill"] = "beginner" });

        var userMessage = client.LastMessages!.Single(message => message.Role == ChatRole.User).Text;
        var systemMessage = client.LastMessages!.Single(message => message.Role == ChatRole.System).Text;

        Assert.Contains("Sichuan", userMessage, StringComparison.Ordinal);
        Assert.Contains("beginner", userMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Sichuan", systemMessage, StringComparison.Ordinal);
    }

    /// <summary>An unsupplied field reads as a complete sentence, not a blank left where a value belonged.</summary>
    [Fact]
    public async Task Unsupplied_inputs_render_as_not_specified()
    {
        var client = FakeChatClient.Returning(TwoConcepts);

        await Run(client, inputs: null);

        var userMessage = client.LastMessages!.Single(message => message.Role == ChatRole.User).Text;
        Assert.Contains("Cuisine: not specified", userMessage, StringComparison.Ordinal);
        Assert.Contains("Creator style: not specified", userMessage, StringComparison.Ordinal);
    }

    /// <summary>A whole-answer warning is re-addressed to nothing; a per-concept one, to that concept's own row.</summary>
    [Fact]
    public async Task A_per_concept_warning_is_reattached_to_that_concepts_add_row()
    {
        const string withWarning = """
            {
              "schemaVersion": "recipe.concepts.v1",
              "concepts": [
                { "title": "Mapo Tofu Reimagined", "summary": "A spicier take.", "distinctnessRationale": "Uses fermented broad bean paste." },
                { "title": "Dan Dan Noodle Bowl", "summary": "A quicker noodle dish.", "distinctnessRationale": "Cold-tossed rather than braised." }
              ],
              "warnings": [ { "kind": "Assumption", "message": "Assumed a wok is available.", "conceptIndex": 0 } ]
            }
            """;

        var client = FakeChatClient.Returning(withWarning);
        var outcome = await Run(client, inputs: null);

        var proposal = outcome.Proposal!;
        var warning = Assert.Single(proposal.Warnings);
        var conceptOneAdd = proposal.Changes.Single(
            change => change.ChangeKind is AiChangeKind.Add && change.AfterValue == "Mapo Tofu Reimagined");

        Assert.Equal(AiWarningKind.Assumption, warning.Kind);
        Assert.Equal(conceptOneAdd.Id, warning.AiStructuredChangeId);
    }

    // ---- correction ----------------------------------------------------------------------------------------

    /// <summary>The handler is oblivious to the correction loop: it just sees the gateway's final outcome.</summary>
    [Fact]
    public async Task A_malformed_answer_is_corrected_and_then_succeeds()
    {
        var client = FakeChatClient.Sequence(() => "not json", () => TwoConcepts);

        var outcome = await Run(client, inputs: null);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Equal(2, outcome.Attempts.Count);
        Assert.True(outcome.Attempts[1].WasSchemaCorrection);
    }

    // ---- workspace isolation ---------------------------------------------------------------------------------

    /// <summary>
    /// Two operations for two workspaces, run separately: each proposal and every one of its structured
    /// changes carries only its own call's workspace, never the other's (tenancy.md).
    /// </summary>
    [Fact]
    public async Task Each_workspaces_proposal_carries_only_its_own_workspace_id()
    {
        var workspaceB = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var outcomeA = await Run(FakeChatClient.Returning(TwoConcepts), inputs: null, workspaceId: Workspace);
        var outcomeB = await Run(FakeChatClient.Returning(TwoConcepts), inputs: null, workspaceId: workspaceB);

        Assert.True(outcomeA.Succeeded, outcomeA.FailureSummary);
        Assert.True(outcomeB.Succeeded, outcomeB.FailureSummary);

        Assert.Equal(Workspace, outcomeA.Proposal!.WorkspaceId);
        Assert.Equal(workspaceB, outcomeB.Proposal!.WorkspaceId);

        Assert.All(outcomeA.Proposal!.Changes, change => Assert.Equal(Workspace, change.WorkspaceId));
        Assert.All(outcomeB.Proposal!.Changes, change => Assert.Equal(workspaceB, change.WorkspaceId));
    }

    // ---- domain failure --------------------------------------------------------------------------------------

    /// <summary>Fewer than two concepts is not "multiple distinct concepts" -- AIREC-001's own floor.</summary>
    [Fact]
    public async Task A_single_concept_answer_fails_without_a_proposal()
    {
        const string oneConcept = """
            {
              "schemaVersion": "recipe.concepts.v1",
              "concepts": [
                { "title": "Mapo Tofu Reimagined", "summary": "A spicier take.", "distinctnessRationale": "n/a" }
              ]
            }
            """;

        var client = FakeChatClient.Returning(oneConcept);
        var outcome = await Run(client, inputs: null);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
        Assert.Equal(AiFailureCategory.DomainInvalid, outcome.FailureCategory);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private const string TwoConcepts = """
        {
          "schemaVersion": "recipe.concepts.v1",
          "concepts": [
            {
              "title": "Mapo Tofu Reimagined",
              "summary": "A spicier take.",
              "distinctnessRationale": "Uses fermented broad bean paste.",
              "assumptions": [ "Assumed silken tofu." ],
              "suggestedIngredients": [ "tofu", "doubanjiang", "ground pork" ]
            },
            {
              "title": "Dan Dan Noodle Bowl",
              "summary": "A quicker noodle dish.",
              "distinctnessRationale": "Cold-tossed rather than braised.",
              "timeBudgetNote": "Fits a 30 minute budget.",
              "skillLevelFit": "Beginner friendly."
            }
          ]
        }
        """;

    private static async Task<AiTaskHandlerOutcome> Run(
        FakeChatClient client, IReadOnlyDictionary<string, string>? inputs, Guid? workspaceId = null)
    {
        const string pipelineKey = "test-ai-concepts";

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
        using var provider = services.BuildServiceProvider();

        var gateway = new AiCompletionGateway(
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

        var handler = new RecipeConceptsAiTaskHandler(
            gateway,
            EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly),
            new StoppedClock());

        var context = new AiTaskExecutionContext(
            Operation, workspaceId ?? Workspace, LeaseToken: Guid.NewGuid(), AiOperationScope.WholeRecipe,
            RecipeId: null, RecipeVersionId: null, CorrelationId: Guid.NewGuid(),
            RenewLeaseAsync: _ => Task.CompletedTask, Inputs: inputs);

        return await handler.HandleAsync(context, TestContext.Current.CancellationToken);
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    /// <summary>A scripted <see cref="IChatClient"/>: no network, no model, fully deterministic.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<Func<string>> _scripted = new();
        private Func<string>? _always;

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public static FakeChatClient Returning(string text) => new() { _always = () => text };

        public static FakeChatClient Sequence(params Func<string>[] texts)
        {
            var client = new FakeChatClient();

            foreach (var text in texts)
            {
                client._scripted.Enqueue(text);
            }

            return client;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            var text = _scripted.Count > 0 ? _scripted.Dequeue()() : _always!();

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "test-model" });
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
