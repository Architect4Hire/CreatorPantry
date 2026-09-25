using System.Diagnostics;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Polly.Registry;

namespace CreatorPantry.Domain.Modules.Ai.Gateways;

/// <summary>Sends one assembled prompt to a model and returns a validated answer or a classified failure.</summary>
public interface IAiCompletionGateway
{
    Task<AiCompletionOutcome> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The only place a model is actually called. Times it, classifies what came back, retries what is worth
/// retrying and nothing else, and records one attempt per call.
/// </summary>
/// <remarks>
/// <para>
/// A gateway by <c>backend.md</c>'s definition: external call, no product decisions. It does not decide what to
/// ask for, what to do with the answer, or whether a proposal is acceptable. It decides how many times to ask
/// and what to write down.
/// </para>
/// <para>
/// <strong>Two retry mechanisms, deliberately distinct.</strong> Transport failures go through the resilience
/// pipeline — timeout, jittered backoff, circuit breaker — configured in ServiceDefaults. A <em>schema</em>
/// failure is different: the provider answered, the answer was unusable, and asking again identically would
/// produce the same thing. That gets exactly one corrective attempt, carrying the reason, and is counted as its
/// own attempt record so the provenance says the answer was corrected.
/// </para>
/// <para>
/// <strong>Nothing here logs content.</strong> Not the envelope, not the response, not a failing value. The
/// envelope is logged through <c>Describe()</c>, which carries segment names, trust levels and sizes.
/// </para>
/// </remarks>
public sealed class AiCompletionGateway(
    IChatClient chatClient,
    IAiFailureClassifier classifier,
    IAiCostEstimator costEstimator,
    IClock clock,
    ResiliencePipelineProvider<string> pipelines,
    AiGatewayOptions options,
    ILogger<AiCompletionGateway> logger) : IAiCompletionGateway
{
    public async Task<AiCompletionOutcome> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var attempts = new List<AiAttemptRecord>();
        var messages = InitialMessages(request.Envelope);

        logger.LogInformation(
            "AI completion starting. correlationId={CorrelationId} template={TemplateId}@{TemplateVersion} envelope=[{Envelope}]",
            request.CorrelationId,
            request.PromptTemplateId,
            request.PromptTemplateVersion,
            request.Envelope.Describe());

        // Bounded by construction: one ask, plus at most one correction. The transport retries live inside the
        // pipeline and do not multiply with this loop.
        for (var attemptNumber = 1; attemptNumber <= options.MaxSchemaCorrections + 1; attemptNumber++)
        {
            var wasCorrection = attemptNumber > 1;
            var (record, response, failure) = await AttemptAsync(
                request, messages, attemptNumber, wasCorrection, cancellationToken);

            attempts.Add(record);

            if (failure is not null)
            {
                return new AiCompletionOutcome { Failure = failure, Attempts = attempts };
            }

            var validation = AiOutputValidator.Validate(
                response!.Text, request.ExpectedSchemaVersion, request.Scope);

            if (validation.Succeeded)
            {
                return new AiCompletionOutcome { Document = validation.Document, Attempts = attempts };
            }

            var invalid = validation.Failure!;
            attempts[^1] = record with
            {
                FailureCategory = invalid.Category,
                FailureSummary = invalid.Message,
            };

            var correctable = invalid.IsCorrectableByReprompt
                && attemptNumber <= options.MaxSchemaCorrections;

            if (!correctable)
            {
                // A domain failure, or a schema failure we have already spent the correction on. Either way,
                // asking again is spending a creator's budget on the same answer.
                logger.LogWarning(
                    "AI output rejected and not corrected. correlationId={CorrelationId} reason={Reason} attempt={Attempt}",
                    request.CorrelationId,
                    invalid.ReasonCode,
                    attemptNumber);

                return new AiCompletionOutcome { Failure = invalid, Attempts = attempts };
            }

            logger.LogInformation(
                "AI output failed its schema; asking for one correction. correlationId={CorrelationId} reason={Reason}",
                request.CorrelationId,
                invalid.ReasonCode);

            // The model's own reply, then what was wrong with it. The reason code and message are ours and are
            // already sanitized; the reply is echoed back because a correction without it has no referent.
            messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            messages.Add(new ChatMessage(ChatRole.User, CorrectionMessage(invalid)));
        }

        // Unreachable: the loop returns on success and on every failure. Present so a future change to the
        // bound fails here rather than returning an outcome with no result.
        throw new InvalidOperationException("The completion loop ended without an outcome.");
    }

    private async Task<(AiAttemptRecord Record, ChatResponse? Response, AiOutputFailure? Failure)> AttemptAsync(
        AiCompletionRequest request,
        IList<ChatMessage> messages,
        int attemptNumber,
        bool wasCorrection,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var elapsed = Stopwatch.StartNew();

        try
        {
            var pipeline = pipelines.GetPipeline(options.ResiliencePipelineKey);

            var response = await pipeline.ExecuteAsync(
                async token => await SendAsync(messages, token, cancellationToken),
                cancellationToken);

            elapsed.Stop();

            return (
                Record(request, attemptNumber, wasCorrection, startedAt, elapsed, response, null),
                response,
                null);
        }
        catch (Exception exception)
        {
            elapsed.Stop();

            // A transient failure that exhausted the pipeline arrives wrapped; an open circuit arrives as
            // Polly's own type. Everything else is classified directly.
            var classification = exception switch
            {
                AiTransientFailureException transient => transient.Classification,

                // The per-attempt deadline, enforced by the pipeline. Named explicitly because it does not
                // derive from OperationCanceledException, so the classifier would otherwise call it a generic
                // provider fault. Not transient here: the pipeline has already spent its retries on it.
                TimeoutRejectedException => new AiFailureClassification(
                    AiFailureCategory.Timeout,
                    false,
                    null,
                    "The provider did not answer within the attempt timeout."),

                BrokenCircuitException => new AiFailureClassification(
                    AiFailureCategory.Provider,
                    false,
                    null,
                    "The provider is failing and calls are being shed."),

                _ => classifier.Classify(exception, cancellationToken),
            };

            logger.LogWarning(
                "AI provider attempt failed. correlationId={CorrelationId} attempt={Attempt} category={Category}",
                request.CorrelationId,
                attemptNumber,
                classification.Category);

            var record = Record(request, attemptNumber, wasCorrection, startedAt, elapsed, null, classification);

            return (
                record,
                null,
                new AiOutputFailure(
                    classification.Category,
                    $"ai.provider.{classification.Category}".ToLowerInvariant(),
                    classification.Summary,
                    classification.IsTransient));
        }
    }

    /// <summary>
    /// Raises a transient failure as <see cref="AiTransientFailureException"/> so the pipeline retries it, and
    /// lets everything else straight out so it does not.
    /// </summary>
    /// <remarks>
    /// Classifying <em>inside</em> the pipeline is what makes "never retry a nontransient failure" true. If the
    /// pipeline saw raw provider exceptions it would retry a safety block and a malformed request alongside a
    /// dropped connection, because at that level they are indistinguishable.
    /// </remarks>
    private async Task<ChatResponse> SendAsync(
        IList<ChatMessage> messages,
        CancellationToken pipelineToken,
        CancellationToken callerToken)
    {
        try
        {
            return await chatClient.GetResponseAsync(messages, cancellationToken: pipelineToken);
        }
        catch (Exception exception)
        {
            var classification = classifier.Classify(exception, callerToken);

            if (classification.IsTransient)
            {
                throw new AiTransientFailureException(classification, exception);
            }

            throw;
        }
    }

    private AiAttemptRecord Record(
        AiCompletionRequest request,
        int attemptNumber,
        bool wasCorrection,
        DateTimeOffset startedAt,
        Stopwatch elapsed,
        ChatResponse? response,
        AiFailureClassification? classification)
    {
        var inputTokens = (int?)response?.Usage?.InputTokenCount;
        var outputTokens = (int?)response?.Usage?.OutputTokenCount;

        return new AiAttemptRecord
        {
            AttemptNumber = attemptNumber,
            ProviderName = options.ProviderName,
            ModelName = response?.ModelId ?? options.ModelName,
            ModelDeployment = options.ModelDeployment,
            PromptTemplateId = request.PromptTemplateId,
            PromptTemplateVersion = request.PromptTemplateVersion,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMilliseconds(elapsed.Elapsed.TotalMilliseconds),
            LatencyMilliseconds = (int)elapsed.Elapsed.TotalMilliseconds,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCost = costEstimator.Estimate(
                response?.ModelId ?? options.ModelName, inputTokens, outputTokens),
            SafetyBlocked = classification?.Category is AiFailureCategory.SafetyBlocked,
            FailureCategory = classification?.Category,
            FailureSummary = classification?.Summary,
            CorrelationId = request.CorrelationId,
            WasSchemaCorrection = wasCorrection,
        };
    }

    private static List<ChatMessage> InitialMessages(PromptEnvelope envelope) =>
    [
        new(ChatRole.System, envelope.SystemMessage),
        new(ChatRole.User, envelope.UserMessage),
    ];

    /// <summary>
    /// The corrective turn. Names what was wrong using our own sanitized message, and restates the contract.
    /// </summary>
    private static string CorrectionMessage(AiOutputFailure failure) =>
        $"""
        That reply could not be accepted: {failure.Message} ({failure.ReasonCode})

        Reply again with one JSON document matching the OUTPUT_SCHEMA segment, and nothing else. Do not wrap it
        in prose or a code fence. The rules in the system message still apply.
        """;
}
