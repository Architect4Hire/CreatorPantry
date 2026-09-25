using System.Net;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Polly.Timeout;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The provider execution wrapper, against a fake <see cref="IChatClient"/>. Covers the seven cases 8.8 names:
/// success, transient retry, timeout, cancellation, rate limit, safety block, malformed output.
/// </summary>
/// <remarks>
/// The pipeline used here has the same shape as the one ServiceDefaults registers but with no delays, so the
/// retry and timeout behaviour is exercised without the tests waiting on real backoff.
/// </remarks>
public sealed class AiCompletionGatewayTests
{
    private const string Schema = "fixture.concepts.v1";
    private static readonly Guid Workspace = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- success ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_answer_returns_the_document_and_one_attempt()
    {
        var client = FakeChatClient.Returning(ValidAnswer);
        var outcome = await Complete(client);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Single(outcome.Document!.Changes);

        var attempt = Assert.Single(outcome.Attempts);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Null(attempt.FailureCategory);
        Assert.False(attempt.WasSchemaCorrection);
        Assert.False(attempt.SafetyBlocked);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task An_attempt_records_the_telemetry_the_execution_row_needs()
    {
        var client = FakeChatClient.Returning(WithUsage(ValidAnswer, input: 1_200, output: 340));
        var correlationId = Guid.NewGuid();

        var outcome = await Complete(client, correlationId: correlationId);
        var attempt = Assert.Single(outcome.Attempts);

        Assert.Equal("test-provider", attempt.ProviderName);
        Assert.Equal("test-model", attempt.ModelName);
        Assert.Equal("test-deployment", attempt.ModelDeployment);
        Assert.Equal("fixture.concepts", attempt.PromptTemplateId);
        Assert.Equal("1.0.0", attempt.PromptTemplateVersion);
        Assert.Equal(correlationId, attempt.CorrelationId);
        Assert.Equal(1_200, attempt.InputTokens);
        Assert.Equal(340, attempt.OutputTokens);
        Assert.InRange(attempt.LatencyMilliseconds, 0, int.MaxValue);
        Assert.True(attempt.CompletedAt >= attempt.StartedAt);
    }

    /// <summary>
    /// A provider that reported no usage did not use zero tokens, and a model with no configured price has an
    /// unknown cost rather than a free one.
    /// </summary>
    [Fact]
    public async Task Absent_usage_and_absent_pricing_produce_nulls_not_zeroes()
    {
        var outcome = await Complete(FakeChatClient.Returning(ValidAnswer));
        var attempt = Assert.Single(outcome.Attempts);

        Assert.Null(attempt.InputTokens);
        Assert.Null(attempt.OutputTokens);
        Assert.Null(attempt.EstimatedCost);
    }

    [Fact]
    public async Task A_configured_price_produces_an_estimate()
    {
        var costs = new AiCostOptions();
        costs.Models["test-model"] = new AiModelPrice(InputPerMillionTokens: 3m, OutputPerMillionTokens: 15m);

        var client = FakeChatClient.Returning(WithUsage(ValidAnswer, input: 1_000_000, output: 100_000));
        var outcome = await Complete(client, costs: costs);

        // 1.0 × 3 + 0.1 × 15 = 4.5
        Assert.Equal(4.5m, Assert.Single(outcome.Attempts).EstimatedCost);
    }

    // ---- transient retry -------------------------------------------------------------------------------

    /// <summary>
    /// A transient transport failure is retried inside the pipeline, so it is one attempt from the wrapper's
    /// point of view and several calls from the provider's.
    /// </summary>
    [Fact]
    public async Task A_transient_failure_is_retried_and_then_succeeds()
    {
        var client = FakeChatClient.Sequence(
            () => throw new HttpRequestException("socket closed", null, HttpStatusCode.ServiceUnavailable),
            () => ValidAnswer);

        var outcome = await Complete(client);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(2, client.Calls);
        Assert.Single(outcome.Attempts);
    }

    [Fact]
    public async Task A_transient_failure_that_never_clears_is_reported_as_a_provider_failure()
    {
        var client = FakeChatClient.AlwaysThrowing(
            () => new HttpRequestException("gone", null, HttpStatusCode.BadGateway));

        var outcome = await Complete(client);

        AssertFailed(outcome, AiFailureCategory.Provider);

        // Three calls: the first plus the pipeline's two retries. Bounded, which is the point.
        Assert.Equal(3, client.Calls);
    }

    /// <summary>
    /// "Never retry nontransient failures blindly." A rejected request is not tried again, however many
    /// retries the pipeline would otherwise allow.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_request_is_not_retried(HttpStatusCode status)
    {
        var client = FakeChatClient.AlwaysThrowing(() => new HttpRequestException("no", null, status));

        var outcome = await Complete(client);

        AssertFailed(outcome, AiFailureCategory.Provider);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task An_exhausted_quota_is_not_retried()
    {
        var client = FakeChatClient.AlwaysThrowing(
            () => new HttpRequestException("no quota", null, HttpStatusCode.PaymentRequired));

        var outcome = await Complete(client);

        AssertFailed(outcome, AiFailureCategory.Quota);
        Assert.Equal(1, client.Calls);
    }

    // ---- timeout ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_that_does_not_answer_in_time_is_recorded_as_a_timeout()
    {
        var client = FakeChatClient.AlwaysAsync(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            return ValidAnswer;
        });

        var outcome = await Complete(client, attemptTimeout: TimeSpan.FromMilliseconds(50));

        AssertFailed(outcome, AiFailureCategory.Timeout);
        Assert.Contains("attempt timeout", outcome.Failure!.Message, StringComparison.Ordinal);
    }

    // ---- cancellation ----------------------------------------------------------------------------------

    /// <summary>
    /// Cancellation is recorded rather than thrown, because the attempt happened and its row has to be
    /// persisted. Swallowing an <see cref="OperationCanceledException"/> is usually wrong; here it is the
    /// reason the wrapper exists.
    /// </summary>
    [Fact]
    public async Task Cancellation_is_recorded_as_an_outcome_rather_than_thrown()
    {
        using var cancellation = new CancellationTokenSource();

        var client = FakeChatClient.AlwaysAsync(async token =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return ValidAnswer;
        });

        var outcome = await Complete(client, cancellationToken: cancellation.Token);

        AssertFailed(outcome, AiFailureCategory.Cancelled);
        Assert.False(outcome.Failure!.IsCorrectableByReprompt);
        Assert.Single(outcome.Attempts);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_retried()
    {
        using var cancellation = new CancellationTokenSource();

        var client = FakeChatClient.AlwaysAsync(async token =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return ValidAnswer;
        });

        var outcome = await Complete(client, cancellationToken: cancellation.Token);

        Assert.Equal(AiFailureCategory.Cancelled, outcome.Failure!.Category);
        Assert.Equal(1, ((FakeChatClient)client).Calls);
    }

    // ---- rate limit ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rate_limit_is_categorised_and_retried()
    {
        var client = FakeChatClient.Sequence(
            () => throw new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests),
            () => ValidAnswer);

        var outcome = await Complete(client);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task A_persistent_rate_limit_is_reported_as_rate_limited()
    {
        var client = FakeChatClient.AlwaysThrowing(
            () => new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests));

        var outcome = await Complete(client);

        AssertFailed(outcome, AiFailureCategory.RateLimited);
        Assert.Equal(3, client.Calls);
    }

    // ---- safety block ----------------------------------------------------------------------------------

    /// <summary>
    /// A safety refusal is never retried: the provider did its job, and asking again spends a creator's budget
    /// arriving at the same refusal. The attempt row records it distinctly from an error.
    /// </summary>
    [Fact]
    public async Task A_safety_block_is_recorded_and_never_retried()
    {
        var client = FakeChatClient.AlwaysThrowing(() => new SafetyRefusedException());

        var outcome = await Complete(client, classifier: new SafetyAwareClassifier());

        AssertFailed(outcome, AiFailureCategory.SafetyBlocked);
        Assert.Equal(1, client.Calls);

        var attempt = Assert.Single(outcome.Attempts);
        Assert.True(attempt.SafetyBlocked);
        Assert.Equal(AiFailureCategory.SafetyBlocked, attempt.FailureCategory);
    }

    // ---- malformed output ------------------------------------------------------------------------------

    /// <summary>
    /// The provider answered; the answer was unusable. Asking again identically would produce the same thing,
    /// so the retry carries the reason — and counts as its own attempt, so provenance can say the answer was
    /// corrected rather than implying the template alone produced it.
    /// </summary>
    [Fact]
    public async Task A_malformed_answer_gets_one_corrective_retry()
    {
        var client = FakeChatClient.Sequence(() => "not json at all", () => ValidAnswer);

        var outcome = await Complete(client);

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, outcome.Attempts.Count);

        Assert.Equal(AiFailureCategory.OutputSchemaInvalid, outcome.Attempts[0].FailureCategory);
        Assert.False(outcome.Attempts[0].WasSchemaCorrection);

        Assert.Null(outcome.Attempts[1].FailureCategory);
        Assert.True(outcome.Attempts[1].WasSchemaCorrection);
    }

    [Fact]
    public async Task The_correction_names_the_reason_without_quoting_the_payload()
    {
        var client = FakeChatClient.Sequence(() => """{"schemaVersion":"wrong.version"}""", () => ValidAnswer);

        await Complete(client);

        var correction = client.LastMessages!.Last();

        Assert.Equal(ChatRole.User, correction.Role);
        Assert.Contains(AiOutputReason.SchemaVersionMismatch, correction.Text, StringComparison.Ordinal);
        Assert.Contains("OUTPUT_SCHEMA", correction.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_correction_is_offered_only_once()
    {
        var client = FakeChatClient.Returning("still not json");

        var outcome = await Complete(client);

        AssertFailed(outcome, AiFailureCategory.OutputSchemaInvalid);
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, outcome.Attempts.Count);
    }

    /// <summary>
    /// A domain failure is never corrected. The model produced a well-formed answer that breaks a rule, and
    /// re-asking is not the remedy.
    /// </summary>
    [Fact]
    public async Task A_domain_invalid_answer_is_not_corrected()
    {
        const string outsideScope = """
            {
              "schemaVersion": "fixture.concepts.v1",
              "changes": [ { "changeKind": "Set", "targetKind": "AssetLink", "targetId": "11111111-1111-1111-1111-111111111111", "fieldName": "altText", "afterValue": "x" } ]
            }
            """;

        var client = FakeChatClient.Returning(outsideScope);

        var outcome = await Complete(client, scope: AiOperationScope.Ingredients);

        AssertFailed(outcome, AiFailureCategory.DomainInvalid);
        Assert.Equal(1, client.Calls);
        Assert.Single(outcome.Attempts);
    }

    [Fact]
    public async Task Corrections_can_be_turned_off_entirely()
    {
        var client = FakeChatClient.Returning("not json");

        var outcome = await Complete(client, maxSchemaCorrections: 0);

        AssertFailed(outcome, AiFailureCategory.OutputSchemaInvalid);
        Assert.Equal(1, client.Calls);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private const string ValidAnswer = """
        {
          "schemaVersion": "fixture.concepts.v1",
          "changes": [ { "changeKind": "Set", "targetKind": "Recipe", "fieldName": "headnote", "afterValue": "A warmer opening." } ]
        }
        """;

    private static void AssertFailed(AiCompletionOutcome outcome, AiFailureCategory category)
    {
        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Document);
        Assert.Equal(category, outcome.Failure!.Category);
        Assert.NotEmpty(outcome.Attempts);
    }

    private static async Task<AiCompletionOutcome> Complete(
        FakeChatClient client,
        IAiFailureClassifier? classifier = null,
        AiCostOptions? costs = null,
        AiOperationScope scope = AiOperationScope.WholeRecipe,
        TimeSpan? attemptTimeout = null,
        int maxSchemaCorrections = 1,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        const string pipelineKey = "test-ai";

        var services = new ServiceCollection();
        services.AddResiliencePipeline(pipelineKey, pipeline => pipeline
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 2,

                // No delay: the behaviour under test is how many times, not how long between.
                Delay = TimeSpan.Zero,
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is AiTransientFailureException),
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = attemptTimeout ?? TimeSpan.FromSeconds(30),
            }));

        using var provider = services.BuildServiceProvider();

        var gateway = new AiCompletionGateway(
            client,
            classifier ?? new DefaultAiFailureClassifier(),
            new ConfiguredAiCostEstimator(costs ?? new AiCostOptions()),
            new StoppedClock(),
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            new AiGatewayOptions
            {
                ResiliencePipelineKey = pipelineKey,
                ProviderName = "test-provider",
                ModelName = "test-model",
                ModelDeployment = "test-deployment",
                MaxSchemaCorrections = maxSchemaCorrections,
            },
            NullLogger<AiCompletionGateway>.Instance);

        var envelope = new PromptEnvelopeBuilder(Workspace)
            .WithTask("Propose a warmer headnote.")
            .WithOutputSchema(AiOutputSchema.Json)
            .Build();

        return await gateway.CompleteAsync(
            new AiCompletionRequest(
                envelope,
                Schema,
                scope,
                "fixture.concepts",
                "1.0.0",
                correlationId ?? Guid.NewGuid()),
            cancellationToken);
    }

    private static ChatResponse WithUsage(string text, int input, int output) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = "test-model",
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        };

    /// <summary>A scripted <see cref="IChatClient"/>: no network, no model, fully deterministic.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _scripted = new();
        private Func<CancellationToken, Task<ChatResponse>>? _always;

        public int Calls { get; private set; }

        /// <summary>The messages of the most recent call, so a corrective turn can be inspected.</summary>
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        /// Distinctly named rather than overloaded: a lambda that only throws satisfies every return type,
        /// so overloads on the return type alone are ambiguous at every throwing call site.
        public static FakeChatClient Returning(string text) =>
            new() { _always = _ => Task.FromResult(Answer(text)) };

        public static FakeChatClient Returning(ChatResponse response) =>
            new() { _always = _ => Task.FromResult(response) };

        public static FakeChatClient AlwaysThrowing(Func<Exception> exception) =>
            new() { _always = _ => throw exception() };

        public static FakeChatClient AlwaysAsync(Func<CancellationToken, Task<string>> text) =>
            new() { _always = async token => Answer(await text(token)) };

        public static FakeChatClient Sequence(params Func<string>[] texts)
        {
            var client = new FakeChatClient();

            foreach (var text in texts)
            {
                client._scripted.Enqueue(_ => Task.FromResult(Answer(text())));
            }

            return client;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastMessages = messages.ToList();

            var next = _scripted.Count > 0 ? _scripted.Dequeue() : _always!;

            return next(cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gateway does not stream.");

        private static ChatResponse Answer(string text) =>
            new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "test-model" };

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A clock that does not move; latency is measured with a Stopwatch, not with this.</summary>
    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Stands in for a provider SDK's content-filter exception.</summary>
    private sealed class SafetyRefusedException() : Exception("content filtered");

    /// <summary>
    /// What a provider-specific classifier does: recognise the SDK's own refusal type. The domain default
    /// cannot, which is precisely why the real one lives in CreatorPantry.AiProvider.
    /// </summary>
    private sealed class SafetyAwareClassifier : DefaultAiFailureClassifier
    {
        public override AiFailureClassification Classify(Exception exception, CancellationToken callerToken) =>
            exception is SafetyRefusedException
                ? new AiFailureClassification(
                    AiFailureCategory.SafetyBlocked, false, null, "The provider's safety filter refused it.")
                : base.Classify(exception, callerToken);
    }
}
