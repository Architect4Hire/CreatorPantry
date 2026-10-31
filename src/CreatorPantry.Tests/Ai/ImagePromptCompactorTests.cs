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
/// The shorter prompt (AP.3.2) against a fake <see cref="IChatClient"/> — no network, no model. Covers the
/// guarantees that are code rather than behaviour: the avoid list never reaches the model and survives word for
/// word, the budget is a ceiling an answer is refused over rather than cut to, the creator's prompt is fenced as
/// untrusted, and every proposal says it was shortened and what was left out.
/// </summary>
public sealed class ImagePromptCompactorTests
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private const string Source =
        "Overhead photograph of a plum galette on a pale linen cloth, soft window light from the left, a dusting of "
        + "icing sugar, a small jug of cream just in frame, two forks on a folded napkin, warm and unhurried.";

    /// <summary>Written oddly on purpose: spacing and punctuation a model "tidying" it would change.</summary>
    private const string Avoid = "No cutlery in the hand ;harsh  flash; plastic props";

    private const int Limit = 7500;
    private const int Budget = 120;

    private const string Shortened = """
        {
          "schemaVersion": "image.prompt.compact.v1",
          "prompt": "Overhead plum galette on pale linen, soft window light from the left, icing sugar, warm and unhurried.",
          "leftOut": ["a small jug of cream just in frame", "two forks on a folded napkin"],
          "warnings": [{ "kind": "Assumption", "message": "The cream and forks were the least visible detail." }]
        }
        """;

    private static readonly string OverBudget = $$"""
        {
          "schemaVersion": "image.prompt.compact.v1",
          "prompt": "{{new string('p', Budget + 1)}}",
          "leftOut": [],
          "warnings": []
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_shorter_prompt_is_the_prompt_row_beside_the_creators_avoid_list_word_for_word()
    {
        var outcome = await Run(FakeChatClient.Returning(Shortened));

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        var proposal = outcome.Proposal!;

        var add = Assert.Single(proposal.Changes, change => change.ChangeKind is AiChangeKind.Add);
        Assert.Equal(AiChangeTargetKind.ImagePrompt, add.TargetKind);
        Assert.StartsWith("Overhead plum galette", add.AfterValue!, StringComparison.Ordinal);
        Assert.True(add.AfterValue!.Length <= Budget);

        var avoid = Assert.Single(proposal.Changes, change => change.ChangeKind is AiChangeKind.Set);
        Assert.Equal(ImagePromptFields.Avoid, avoid.FieldName);
        Assert.Equal(Avoid, avoid.AfterValue);
        Assert.Equal(add.TargetId, avoid.TargetId);

        Assert.Equal(ImagePromptCompactor.TemplateId, proposal.PromptTemplateId);
        Assert.Equal("1.0.0", proposal.PromptTemplateVersion);
        Assert.Null(proposal.SourceRecipeVersionId);
    }

    /// <summary>
    /// <strong>The avoid list is never shown to the model</strong>, so no answer can drop or reword it; the
    /// creator's prompt is, in the untrusted fence of the user message and nowhere in the system message.
    /// </summary>
    [Fact]
    public async Task The_model_sees_the_prompt_fenced_as_untrusted_and_never_the_avoid_list()
    {
        var client = FakeChatClient.Returning(Shortened);

        await Run(client);

        var system = string.Concat(client.LastMessages!.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        var user = string.Concat(client.LastMessages!.Where(message => message.Role == ChatRole.User).Select(message => message.Text));

        Assert.DoesNotContain("plum galette", system, StringComparison.Ordinal);
        Assert.Contains("plum galette", user, StringComparison.Ordinal);
        Assert.Contains("UNTRUSTED", user, StringComparison.Ordinal);

        foreach (var text in new[] { system, user })
        {
            Assert.DoesNotContain("cutlery", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("plastic props", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The model is asked for nine tenths of the budget and held to all of it, so an answer that overshoots a
    /// little still lands.
    /// </summary>
    [Fact]
    public async Task The_model_is_asked_for_less_than_the_budget_it_is_held_to()
    {
        var client = FakeChatClient.Returning(Shortened);

        await Run(client);

        var system = string.Concat(client.LastMessages!.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        var aim = Budget * AiPolicy.ImagePromptCompactionAimPercent / 100;

        Assert.Contains($"The prompt is {Source.Length} characters", system, StringComparison.Ordinal);
        Assert.Contains($"{aim} characters", system, StringComparison.Ordinal);
        Assert.Contains($"about {aim / AiPolicy.ImagePromptCompactionCharactersPerWord}", system, StringComparison.Ordinal);
        Assert.DoesNotContain($"{Budget} characters", system, StringComparison.Ordinal);
    }

    /// <summary>
    /// The schema the model is shown pins the one version the validator accepts, so the corrective re-ask is not
    /// spent on a guessed "1.0" — every first answer in the first live run.
    /// </summary>
    [Fact]
    public async Task The_schema_shown_pins_the_version_the_answer_must_declare()
    {
        var client = FakeChatClient.Returning(Shortened);

        await Run(client);

        var system = string.Concat(client.LastMessages!.Where(message => message.Role == ChatRole.System).Select(message => message.Text));

        Assert.Contains("\"const\": \"image.prompt.compact.v1\"", system, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>Over the budget is refused, never cut</strong>: one corrective re-ask, and an answer still over
    /// fails the operation with no proposal — there is nothing for a creator to mistake for their prompt.
    /// </summary>
    [Fact]
    public async Task An_answer_over_the_budget_is_asked_again_once_and_then_fails_rather_than_being_cut()
    {
        var client = FakeChatClient.Returning(OverBudget);

        var outcome = await Run(client);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Proposal);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task An_answer_over_the_budget_corrected_on_the_re_ask_is_the_proposal()
    {
        var client = FakeChatClient.Answering(OverBudget, Shortened);

        var outcome = await Run(client);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Equal(2, outcome.Attempts.Count);
        Assert.StartsWith(
            "Overhead plum galette",
            Assert.Single(outcome.Proposal!.Changes, change => change.ChangeKind is AiChangeKind.Add).AfterValue!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>Never silent.</strong> The server says first that this was shortened by AI and the creator's own
    /// prompt is unchanged; each piece the model left out is its own line; the model's own warnings follow.
    /// </summary>
    [Fact]
    public async Task Every_proposal_says_it_was_shortened_and_lists_what_was_left_out()
    {
        var outcome = await Run(FakeChatClient.Returning(Shortened));

        var warnings = outcome.Proposal!.Warnings.OrderBy(warning => warning.SortOrder).Select(warning => warning.Message).ToList();

        Assert.StartsWith("Shortened by AI to fit the 7,500 characters", warnings[0], StringComparison.Ordinal);
        Assert.Contains("Your own prompt is unchanged", warnings[0], StringComparison.Ordinal);
        Assert.Contains("avoid list is carried over word for word", warnings[0], StringComparison.Ordinal);
        Assert.Equal("Left out to fit: a small jug of cream just in frame", warnings[1]);
        Assert.Equal("Left out to fit: two forks on a folded napkin", warnings[2]);
        Assert.Equal("The cream and forks were the least visible detail.", warnings[3]);
        Assert.Equal(4, warnings.Count);
    }

    /// <summary>
    /// <strong>What the model added is named by the server</strong>: a shorter prompt carrying a word the creator
    /// never wrote gets a warning naming it — and one that only reworded, or used a plural, gets none.
    /// </summary>
    [Fact]
    public async Task Words_the_creator_did_not_write_are_named_in_a_warning()
    {
        const string added = """
            {
              "schemaVersion": "image.prompt.compact.v1",
              "prompt": "Overhead plum galette on pale linen beside a copper saucepan, soft window light from the left, warm.",
              "leftOut": [],
              "warnings": []
            }
            """;

        var withAddition = await Run(FakeChatClient.Answering(added));
        var faithful = await Run(FakeChatClient.Returning(Shortened));

        var named = Assert.Single(
            withAddition.Proposal!.Warnings,
            warning => warning.Message.StartsWith("Words in the shorter prompt", StringComparison.Ordinal));
        Assert.Contains("copper, saucepan", named.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("beside", named.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(
            faithful.Proposal!.Warnings,
            warning => warning.Message.StartsWith("Words in the shorter prompt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("two forks on a napkin", "a fork on napkins", "")]
    [InlineData("a jug of cream", "a pitcher of cream", "pitcher")]
    [InlineData("soft light", "Soft LIGHT, gentle", "gentle")]
    public void New_words_ignore_case_plurals_and_short_joins(string source, string shorter, string expected)
    {
        Assert.Equal(expected, string.Join(",", ImagePromptCompactor.NewWords(source, shorter)));
    }

    /// <summary>With no avoid list there is nothing to carry over: one row, and no claim that one was kept.</summary>
    [Fact]
    public async Task With_no_avoid_list_the_proposal_is_the_prompt_alone()
    {
        var outcome = await Run(FakeChatClient.Returning(Shortened), avoid: null);

        Assert.Single(outcome.Proposal!.Changes);
        Assert.DoesNotContain(
            outcome.Proposal.Warnings,
            warning => warning.Message.Contains("avoid list", StringComparison.Ordinal));
    }

    /// <summary>
    /// A request with no budget, or one below the shortest prompt there is, is refused before anything is asked
    /// of a model: there is no answer it could pass.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("39")]
    [InlineData("not a number")]
    public async Task No_usable_budget_is_refused_before_any_provider_call(string? budget)
    {
        var client = FakeChatClient.Returning(Shortened);

        var outcome = await Run(client, budget: budget);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AiFailureCategory.Validation, outcome.FailureCategory);
        Assert.Equal(0, client.Calls);
    }

    /// <summary>
    /// The handler composing prompts hands a shortening request to this, without asking for a concept it does not
    /// have — the same task, routed by its inputs.
    /// </summary>
    [Fact]
    public async Task The_image_prompt_handler_routes_a_shortening_request_here()
    {
        var gateway = Gateway(FakeChatClient.Returning(Shortened));

        // Every dependency composition needs is absent: reaching one would throw.
        var handler = new ImagePromptAiTaskHandler(
            gateway,
            EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly),
            operations: null!,
            brandContext: null!,
            recipes: null!,
            passages: null!,
            creativeContexts: null!,
            new StoppedClock());

        var outcome = await handler.HandleAsync(Context(Inputs(Avoid, Budget.ToString())), Ct);

        Assert.True(outcome.Succeeded, outcome.FailureSummary);
        Assert.Equal(ImagePromptCompactor.TemplateId, outcome.Proposal!.PromptTemplateId);
    }

    private static async Task<AiTaskHandlerOutcome> Run(
        IChatClient client, string? avoid = Avoid, string? budget = "120")
    {
        var compactor = new ImagePromptCompactor(
            Gateway(client), EmbeddedPromptTemplateStore.Load(typeof(AiPolicy).Assembly), new StoppedClock());

        return await compactor.HandleAsync(Context(Inputs(avoid, budget)), Ct);
    }

    private static Dictionary<string, string> Inputs(string? avoid, string? budget)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ImagePromptCompactionInputs.SourcePrompt] = Source,
            [ImagePromptCompactionInputs.CharacterLimit] = Limit.ToString(),
            [ImagePromptCompactionInputs.QualityClass] = "Draft",
        };

        if (avoid is not null)
        {
            inputs[ImagePromptCompactionInputs.Avoid] = avoid;
        }

        if (budget is not null)
        {
            inputs[ImagePromptCompactionInputs.PromptBudget] = budget;
        }

        return inputs;
    }

    private static AiTaskExecutionContext Context(Dictionary<string, string> inputs) => new(
        Guid.NewGuid(),
        WorkspaceA,
        LeaseToken: Guid.NewGuid(),
        AiOperationScope.NotApplicable,
        RecipeId: null,
        RecipeVersionId: null,
        CorrelationId: Guid.NewGuid(),
        RenewLeaseAsync: _ => Task.CompletedTask,
        Inputs: inputs);

    private static AiCompletionGateway Gateway(IChatClient client)
    {
        const string pipelineKey = "test-ai-image-prompt-compaction";

        var services = new ServiceCollection();
        services.AddResiliencePipeline(pipelineKey, pipeline => pipeline
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) }));

        // Not disposed here: the gateway holds the pipeline for the test's life, and the provider is small.
        var pipelines = services.BuildServiceProvider();

        return new AiCompletionGateway(
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
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    /// <summary>Answers in turn, repeating the last; records what it was last asked and how often.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        private string[] _answers = [];

        public int Calls { get; private set; }

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public static FakeChatClient Returning(string text) => new() { _answers = [text] };

        public static FakeChatClient Answering(params string[] answers) => new() { _answers = answers };

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            var answer = _answers[Math.Min(Calls, _answers.Length - 1)];
            Calls++;

            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)) { ModelId = "test-model" });
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
